using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ApiSiteAnalyzer.Contract;

namespace ApiSiteAnalyzer.Host
{
    /// <summary>
    /// 业务核心加载器——把 <c>ApiSiteAnalyzer.Core.dll</c> 装进**可回收 ALC**，支持进程内热重载。
    /// <para>
    /// 为什么走影子副本：Windows 上已加载程序集的 dll 被 CLR 持锁，原地覆盖 Core.dll 会失败。
    /// 每次加载把编译产物（Core + 第三方依赖）复制到 <c>data/run/core/core-&lt;时间戳&gt;/</c> 再加载——
    /// 编译区与加载区解耦，于是「边跑边编译」与「不重启进程换代码」同时成立。
    /// </para>
    /// <para>
    /// 边界：宿主自身（入口 / 加载器 / 契约）改动无法自替换，需一次进程重启（部署脚本负责）；
    /// 业务面（Web / Store / Sites / Browser / Collect / Config）改动可在进程内完成。
    /// </para>
    /// </summary>
    internal sealed class CoreLoader : IDisposable
    {
        /// <summary>核心程序集文件名。</summary>
        private const string CoreAssemblyFile = "ApiSiteAnalyzer.Core.dll";

        /// <summary>核心入口类型（反射调用，跨 ALC 的唯一入口）。</summary>
        private const string CoreTypeName = "ApiSiteAnalyzer.CoreEntry";

        /// <summary>契约程序集名——必须共享默认 ALC，不可在自定义 ALC 里加载第二份。</summary>
        private const string ContractAssemblyName = "ApiSiteAnalyzer.Contract";

        /// <summary>编译产物目录（含 Core.dll 与第三方依赖）。</summary>
        private readonly string _sourceDir;

        /// <summary>影子副本根（<c>data/run/core</c>）。</summary>
        private readonly string _shadowRoot;

        /// <summary>配置文件路径（每次装载都读一次——config.json 是部署期声明，换核不改其热读性）。</summary>
        private readonly string _configPath;

        /// <summary>程序目录（宿主传入，供核心做配置回落）。</summary>
        private readonly string _baseDir;

        /// <summary>当前 ALC。</summary>
        private AssemblyLoadContext? _alc;

        /// <summary>当前核心实例。</summary>
        private IShellCore? _core;

        /// <summary>ALC 弱引用（卸载是否干净的可观测判据）。</summary>
        private WeakReference? _alcRef;

        /// <summary>本次加载的影子路径（日志与回滚用）。</summary>
        private string _loadedFrom = "";

        /// <summary>连续「ALC 未回收」次数——达到阈值后由宿主改走进程重启（内存归零）。</summary>
        private int _leakCount;

        /// <summary>
        /// 构造加载器（不加载）。
        /// </summary>
        /// <param name="sourceDir">核心编译产物目录。</param>
        /// <param name="shadowRoot">影子副本根（<c>data/run/core</c>）。</param>
        /// <param name="configPath">配置文件路径（装载时读一次）。</param>
        /// <param name="baseDir">程序目录。</param>
        public CoreLoader(string sourceDir, string shadowRoot, string configPath, string baseDir)
        {
            _sourceDir = sourceDir;
            _shadowRoot = shadowRoot;
            _configPath = configPath;
            _baseDir = baseDir;
        }

        /// <summary>当前核心实例（未加载为 null）。</summary>
        public IShellCore? Core
        {
            get { return _core; }
        }

        /// <summary>当前影子副本路径（诊断用）。</summary>
        public string LoadedFrom
        {
            get { return _loadedFrom; }
        }

        /// <summary>连续未回收次数（宿主据此决定何时走进程重启兜底）。</summary>
        public int LeakCount
        {
            get { return _leakCount; }
        }

        /// <summary>核心编译产物目录（日志用）。</summary>
        public string SourceDir
        {
            get { return _sourceDir; }
        }

        /// <summary>
        /// 从编译产物目录加载核心（先复制影子副本）。
        /// </summary>
        /// <param name="error">失败原因（成功为空串）。</param>
        /// <returns>加载成功为 true。</returns>
        public bool Load(out string error)
        {
            string source = Path.Combine(_sourceDir, CoreAssemblyFile);
            if (File.Exists(source) == false)
            {
                error = "找不到核心程序集：" + source;
                return false;
            }

            string shadowDir = Path.Combine(_shadowRoot, "core-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            try
            {
                Directory.CreateDirectory(shadowDir);
                CopyTree(_sourceDir, shadowDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = "复制影子副本失败：" + shadowDir + " :: " + ex.Message;
                return false;
            }

            return LoadFrom(Path.Combine(shadowDir, CoreAssemblyFile), out error);
        }

        /// <summary>
        /// 从指定路径加载核心（影子副本 / 回滚路径）。
        /// </summary>
        /// <param name="target">dll 绝对路径。</param>
        /// <param name="error">失败原因（成功为空串）。</param>
        /// <returns>加载成功为 true。</returns>
        public bool LoadFrom(string target, out string error)
        {
            error = "";
            AssemblyLoadContext? alc = null;
            try
            {
                string dir = Path.GetDirectoryName(target) ?? "";
                alc = new CoreLoadContext(dir);
                Assembly asm = alc.LoadFromAssemblyPath(target);
                Type entry = asm.GetType(CoreTypeName, true)!;
                MethodInfo? create = entry.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null,
                    Type.EmptyTypes, null);
                if (create == null)
                {
                    error = "核心入口签名不匹配：" + CoreTypeName + ".Create()";
                    alc.Unload();
                    return false;
                }

                object? made = create.Invoke(null, null);
                IShellCore? core = made as IShellCore;
                if (core == null)
                {
                    error = "核心未实现 IShellCore（契约版本不一致？）";
                    alc.Unload();
                    return false;
                }

                // 装载配置与站点——每次装载（首次 / 换核 / 回滚）都要走一次，
                // 否则新实例处于「未 Load」状态，Start 直接抛 InvalidOperationException（首轮实测）
                try
                {
                    core.Load(_configPath, _baseDir);
                }
                catch (Exception ex)
                {
                    error = "核心装载失败：" + ex.GetType().Name + ": " + ex.Message;
                    alc.Unload();
                    return false;
                }

                _alc = alc;
                _alcRef = new WeakReference(alc);
                _core = core;
                _loadedFrom = target;
                Log("core-loaded", "version=" + core.Version + " | from=" + target);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is BadImageFormatException || ex is TargetInvocationException
                || ex is TypeLoadException || ex is MissingMethodException
                || ex is FileNotFoundException || ex is InvalidOperationException
                || ex is ArgumentException || ex is DllNotFoundException)
            {
                error = ex.GetType().Name + ": " + ex.Message
                    + (ex.InnerException == null ? "" : " :: " + ex.InnerException.GetType().Name
                        + ": " + ex.InnerException.Message);
                if (alc != null)
                {
                    try
                    {
                        alc.Unload();
                    }
                    catch (InvalidOperationException inner)
                    {
                        Console.Error.WriteLine("alc-unload-failed: " + inner.Message);
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// 卸载核心——先停服务再卸载 ALC；卸载不干净只出声，不阻断下一次加载。
        /// </summary>
        /// <param name="error">失败 / 告警原因（成功为空串）。</param>
        /// <returns>完全干净卸载为 true。</returns>
        public bool Unload(out string error)
        {
            // 🔴 分三段是必须的：StopCore / UnloadAlc 各自在独立栈帧里丢掉引用，
            // 再在第三个方法里做 GC 探测——同帧持有 ALC 或 IShellCore 局部时，
            // Debug 构建不会提前释放它们，可回收 ALC 永远回收不掉（壳侧首轮实测）。
            StopCore();
            UnloadAlc();
            WeakReference? w = _alcRef;
            _alcRef = null;
            string previous = _loadedFrom;
            _loadedFrom = "";
            bool clean = CollectAndCheck(w, out error);
            _leakCount = clean ? 0 : _leakCount + 1;
            if (clean)
            {
                DeleteShadow(previous);
            }

            return clean;
        }

        /// <summary>
        /// 热重载——停旧服务 → 卸载 ALC → 加载新产物 → 起服务；失败回滚到上一份影子副本。
        /// </summary>
        /// <param name="note">成功回执（含加载路径与卸载洁净度）。</param>
        /// <param name="error">失败原因（成功为空串）。</param>
        /// <returns>重载成功为 true。</returns>
        public bool Reload(out string note, out string error)
        {
            note = "";
            error = "";
            string previous = _loadedFrom;

            string unloadWarn = "";
            bool clean = Unload(out unloadWarn);

            string loadError = "";
            if (Load(out loadError) == false)
            {
                error = "加载新核心失败：" + loadError;
                if (previous.Length > 0 && File.Exists(previous))
                {
                    string rollbackError = "";
                    if (LoadFrom(previous, out rollbackError))
                    {
                        try
                        {
                            _core!.Start(false);
                            error = error + "（已回滚旧核心：" + _core.Version + "）";
                        }
                        catch (Exception ex) when (ex is InvalidOperationException
                            || ex is System.Net.Sockets.SocketException || ex is IOException)
                        {
                            error = error + "；回滚起服务也失败：" + ex.Message;
                        }
                    }
                    else
                    {
                        error = error + "；回滚失败：" + rollbackError;
                    }
                }

                Log("core-reload-fail", error);
                return false;
            }

            try
            {
                _core!.Start(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                || ex is System.Net.Sockets.SocketException || ex is IOException)
            {
                error = "新核心起服务失败：" + ex.GetType().Name + ": " + ex.Message;
                Log("core-reload-fail", error);
                return false;
            }

            note = "core=" + _core!.Version + " | from=" + _loadedFrom
                + " | unload=" + (clean ? "clean" : unloadWarn);
            Log("core-reloaded", note);
            return true;
        }

        /// <summary>释放（卸载）。</summary>
        public void Dispose()
        {
            string ignored;
            Unload(out ignored);
        }

        /// <summary>停核心并丢掉引用——独立栈帧，返回后局部引用消失（ALC 回收前提）。</summary>
        private void StopCore()
        {
            IShellCore? core = _core;
            _core = null;
            if (core == null)
            {
                return;
            }

            try
            {
                core.Stop();
                core.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                Log("core-stop-warn", ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>卸载 ALC——独立栈帧，返回后局部引用消失。</summary>
        private void UnloadAlc()
        {
            AssemblyLoadContext? alc = _alc;
            _alc = null;
            if (alc == null)
            {
                return;
            }

            alc.Unload();
        }

        /// <summary>
        /// 多轮 GC 并判断 ALC 是否真的回收——判据用弱引用，不引入新强引用。
        /// </summary>
        /// <param name="w">ALC 弱引用。</param>
        /// <param name="error">未回收时的告警文本（成功为空串）。</param>
        /// <returns>已回收为 true。</returns>
        private static bool CollectAndCheck(WeakReference? w, out string error)
        {
            error = "";
            System.Threading.Thread.Sleep(80);
            for (int i = 0; i < 4; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }

            System.Threading.Thread.Sleep(40);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            if (w != null && w.IsAlive)
            {
                error = "ALC 未回收（仍有外部引用，内存会涨一份）";
                Log("core-unloaded", error);
                return false;
            }

            Log("core-unloaded", "clean");
            return true;
        }

        /// <summary>
        /// 递归复制影子副本——跳过契约程序集（契约必须共享默认 ALC 的那一份，复制会把类型身份劈成两份）。
        /// </summary>
        /// <param name="src">源目录。</param>
        /// <param name="dst">目标目录。</param>
        private static void CopyTree(string src, string dst)
        {
            foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith(ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(src, file);
                string target = Path.Combine(dst, relative);
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(file, target, true);
            }
        }

        /// <summary>清理已回收的影子副本目录——卸载干净后才删，删不掉只出声（不阻断）。</summary>
        /// <param name="dllPath">影子副本 dll 路径。</param>
        private static void DeleteShadow(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath))
            {
                return;
            }

            try
            {
                string? dir = Path.GetDirectoryName(dllPath);
                if (dir != null && Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    Log("core-shadow-deleted", dir);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is ArgumentException)
            {
                Console.Error.WriteLine("shadow-delete-skip: " + ex.Message);
            }
        }

        /// <summary>写一行运行日志（落 <c>data/logs/host.log</c> 之外的 stdout——部署脚本据此取证）。</summary>
        /// <param name="tag">事件名。</param>
        /// <param name="message">明细。</param>
        private static void Log(string tag, string message)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + tag + " · " + message);
        }

        /// <summary>
        /// 自定义 ALC——Core 的私有依赖从影子目录解析；契约交回默认 ALC；框架程序集回落默认。
        /// </summary>
        private sealed class CoreLoadContext : AssemblyLoadContext
        {
            /// <summary>影子副本目录。</summary>
            private readonly string _dir;

            /// <summary>构造（可回收）。</summary>
            /// <param name="dir">影子副本目录。</param>
            public CoreLoadContext(string dir) : base("ApiSiteAnalyzer.Core", true)
            {
                _dir = dir;
                ResolvingUnmanagedDll += ResolveNative;
            }

            /// <summary>程序集解析——契约共享默认 ALC，私有依赖走影子目录，其余回落默认。</summary>
            /// <param name="name">程序集名。</param>
            /// <returns>程序集；null = 交回默认 ALC。</returns>
            protected override Assembly? Load(AssemblyName name)
            {
                if (string.Equals(name.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (name.Name == null)
                {
                    return null;
                }

                string candidate = Path.Combine(_dir, name.Name + ".dll");
                return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
            }

            /// <summary>非托管库解析——e_sqlite3 等 native 从影子目录取（保持换核后整份自洽）。</summary>
            /// <param name="assembly">请求方程序集。</param>
            /// <param name="name">库名。</param>
            /// <returns>库句柄；Zero = 交回系统解析。</returns>
            private IntPtr ResolveNative(Assembly assembly, string name)
            {
                string[] candidates =
                {
                    Path.Combine(_dir, "runtimes", "win-x64", "native", name),
                    Path.Combine(_dir, "runtimes", "win-x64", "native", name + ".dll"),
                    Path.Combine(_dir, name),
                    Path.Combine(_dir, name + ".dll"),
                };
                foreach (string path in candidates)
                {
                    if (File.Exists(path))
                    {
                        return NativeLibrary.Load(path);
                    }
                }

                return IntPtr.Zero;
            }
        }
    }
}
