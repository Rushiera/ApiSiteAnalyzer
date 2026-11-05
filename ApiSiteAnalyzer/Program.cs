using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using ApiSiteAnalyzer.Contract;

namespace ApiSiteAnalyzer.Host
{
    /// <summary>
    /// 宿主入口——**薄层**：解析参数 → 经 <see cref="CoreLoader"/> 装 Core → 转发 / 心跳。
    /// <para>
    /// 本程序集只引用 <c>ApiSiteAnalyzer.Contract</c>，不静态引用 Core——这是 Core 能被卸载的前提。
    /// 两层职责：serve（常驻 + 心跳消费 reload / stop + 泄漏兜底自重启）与 CLI（转发到 <c>RunCommand</c>）。
    /// </para>
    /// </summary>
    public static class Program
    {
        /// <summary>退出码 3——泄漏兜底 / 无可回滚核心时走进程自重启。</summary>
        private const int RestartExitCode = 3;

        /// <summary>心跳间隔（秒）——reload 申请与停止请求的消费周期。</summary>
        private const int BeatSeconds = 5;

        /// <summary>连续未回收阈值——达到即走进程重启兜底。</summary>
        private const int LeakRestartThreshold = 3;

        /// <summary>入口。</summary>
        /// <param name="args">命令行参数。</param>
        /// <returns>退出码。</returns>
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            string verb = args.Length > 0 ? args[0] : "serve";

            // [段1] 选项与位置参数分开——`fetch --full` 里 --full 不是配置文件路径
            bool full = false;
            string siteFilter = "";
            string configArg = "";
            string coreArg = "";
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--full", StringComparison.OrdinalIgnoreCase))
                {
                    full = true;
                }
                else if (args[i].StartsWith("--site=", StringComparison.OrdinalIgnoreCase))
                {
                    siteFilter = args[i].Substring("--site=".Length);
                }
                else if (args[i].StartsWith("--core=", StringComparison.OrdinalIgnoreCase))
                {
                    coreArg = args[i].Substring("--core=".Length);
                }
                else if (args[i].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    configArg = args[i].Substring("--config=".Length);
                }
                else if (configArg.Length == 0)
                {
                    configArg = args[i];
                }
            }

            try
            {
                string configPath = ResolveConfigPath(configArg);
                string dataDir = ResolveDataDir(configPath);
                string sourceDir = ResolveCoreDir(coreArg);

                var loader = new CoreLoader(sourceDir, Path.Combine(dataDir, "run", "core"),
                    configPath, AppContext.BaseDirectory);
                string error = "";
                if (!loader.Load(out error))
                {
                    Console.Error.WriteLine("核心加载失败：" + error);
                    return 1;
                }

                // 配置与站点在 loader 内装载（首次 / 换核 / 回滚同一路径）——这里不再重复 Load
                IShellCore core = loader.Core!;

                if (!string.Equals(verb, "serve", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return core.RunCommand(verb, BuildOptionsJson(full, siteFilter));
                    }
                    finally
                    {
                        loader.Dispose();
                    }
                }

                int rc = RunServe(loader, core, dataDir, args);
                if (rc == RestartExitCode)
                {
                    // 自重启的工作目录 = config.json 所在目录（不是 exe 目录）——
                    // 非单文件布局里 exe 在 slot_A\，config.json 在上一级，用 exe 目录会找不到配置（首轮实测）
                    SpawnDetached(args, Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? dataDir);
                }

                return rc;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is InvalidOperationException
                || ex is IOException || ex is UnauthorizedAccessException || ex is TypeLoadException)
            {
                Console.Error.WriteLine("失败：" + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 常驻主路径——起服务 → 心跳消费 reload / stop → 兜底自重启。
        /// </summary>
        /// <param name="loader">核心加载器。</param>
        /// <param name="core">已 Load 的核心。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <param name="args">原始参数（自重启用）。</param>
        /// <returns>退出码（0 正常 / 1 启动失败 / 3 自重启）。</returns>
        private static int RunServe(CoreLoader loader, IShellCore core, string dataDir, string[] args)
        {
            string runDir = Path.Combine(dataDir, "run");
            Directory.CreateDirectory(runDir);
            string reloadRequest = Path.Combine(runDir, "reload.request");
            string reloadResult = Path.Combine(runDir, "reload.result.json");
            string stopSignal = Path.Combine(runDir, "serve.stop");

            bool stop = false;
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                stop = true;
            };

            try
            {
                core.Start(true);
                Console.WriteLine("ApiSiteAnalyzer 已启动 · pid " + Environment.ProcessId + " · core " + core.Version);
                Console.WriteLine("核心来源：" + loader.LoadedFrom);
                WriteSnapshot(runDir, core, loader);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                || ex is System.Net.Sockets.SocketException || ex is IOException)
            {
                Console.Error.WriteLine("启动失败：" + ex.GetType().Name + ": " + ex.Message);
                loader.Dispose();
                return 1;
            }

            while (!stop)
            {
                Thread.Sleep(BeatSeconds * 1000);
                if (stop)
                {
                    break;
                }

                if (File.Exists(stopSignal))
                {
                    TryDelete(stopSignal);
                    Console.WriteLine("收到 serve.stop——优雅退出");
                    break;
                }

                if (!File.Exists(reloadRequest))
                {
                    continue;
                }

                TryDelete(reloadRequest);
                Console.WriteLine("收到 reload 申请——开始换核");
                string note = "";
                string error = "";
                bool ok = loader.Reload(out note, out error);
                WriteReloadResult(reloadResult, ok, note, error, loader);

                if (!ok && loader.Core == null)
                {
                    Console.WriteLine("换核失败且无回滚核心——走进程重启");
                    loader.Dispose();
                    return RestartExitCode;
                }

                if (loader.LeakCount >= LeakRestartThreshold)
                {
                    Console.WriteLine("连续 " + loader.LeakCount + " 次 ALC 未回收——走进程重启兜底");
                    loader.Dispose();
                    return RestartExitCode;
                }

                if (loader.Core != null)
                {
                    core = loader.Core;
                    WriteSnapshot(runDir, core, loader);
                }
            }

            loader.Dispose();
            return 0;
        }

        /// <summary>解析配置文件路径——当前工作目录优先，缺失回落程序目录（与 Core 同规则）。</summary>
        /// <param name="given">命令行给的路径（空 = 自动）。</param>
        /// <returns>配置文件路径。</returns>
        private static string ResolveConfigPath(string given)
        {
            if (given.Length > 0)
            {
                return given;
            }

            if (File.Exists("config.json"))
            {
                return "config.json";
            }

            string beside = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(beside))
            {
                return beside;
            }

            throw new FileNotFoundException("找不到配置文件 config.json（当前工作目录与程序目录都没有）");
        }

        /// <summary>数据目录（config 同级 data）——宿主据此定位 run 目录。</summary>
        /// <param name="configPath">配置文件路径。</param>
        /// <returns>数据目录绝对路径。</returns>
        private static string ResolveDataDir(string configPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
            return Path.Combine(dir, "data");
        }

        /// <summary>
        /// 核心编译产物目录——显式参数优先，其次 <c>core/&lt;槽名&gt;/</c>，再 <c>core/</c>，最后 exe 同级。
        /// </summary>
        /// <param name="given">显式目录（空 = 探测）。</param>
        /// <returns>目录路径。</returns>
        private static string ResolveCoreDir(string given)
        {
            if (given.Length > 0)
            {
                return given;
            }

            string baseDir = AppContext.BaseDirectory;
            // 非单文件发布后槽位是**目录**（apphost 固定按 ApiSiteAnalyzer.dll 找伴生文件，exe 不能改名），
            // 核心落 <槽目录>/core/，宿主从那里取影子副本源
            string[] candidates =
            {
                Path.Combine(baseDir, "core"),
                baseDir,
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "ApiSiteAnalyzer.Core.dll")))
                {
                    return candidate;
                }
            }

            return candidates[0];
        }

        /// <summary>构造 CLI 选项 JSON。</summary>
        /// <param name="full">fetch 全量重扫。</param>
        /// <param name="siteFilter">单站过滤。</param>
        /// <returns>JSON 文本。</returns>
        private static string BuildOptionsJson(bool full, string siteFilter)
        {
            var payload = new { full, site = siteFilter };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>写换核结果（成功失败都落——失败必须可见）。</summary>
        /// <param name="path">结果文件路径。</param>
        /// <param name="ok">是否成功。</param>
        /// <param name="note">成功回执。</param>
        /// <param name="error">失败原因。</param>
        /// <param name="loader">加载器（取版本 / 影子路径 / 泄漏计数）。</param>
        private static void WriteReloadResult(string path, bool ok, string note, string error, CoreLoader loader)
        {
            var payload = new
            {
                ok,
                note,
                error,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                pid = Environment.ProcessId,
                coreVersion = loader.Core == null ? "" : loader.Core.Version,
                loadedFrom = loader.LoadedFrom,
                leakCount = loader.LeakCount,
            };
            try
            {
                File.WriteAllText(path,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("reload.result.json 落盘失败：" + ex.Message);
            }
        }

        /// <summary>写核心状态快照（pid / 版本 / 端口 / 影子来源）。</summary>
        /// <param name="runDir">run 目录。</param>
        /// <param name="core">核心实例。</param>
        /// <param name="loader">加载器。</param>
        private static void WriteSnapshot(string runDir, IShellCore core, CoreLoader loader)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(core.SnapshotJson());
                string body = doc.RootElement.GetRawText();
                File.WriteAllText(Path.Combine(runDir, "core.state.json"), body, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("core.state.json 落盘失败：" + ex.Message);
            }
        }

        /// <summary>起一份脱离当前进程的新实例（退出码 3 兜底）。</summary>
        /// <param name="args">原始参数。</param>
        /// <param name="workingDir">新实例的工作目录（config.json 所在目录）。</param>
        private static void SpawnDetached(string[] args, string workingDir)
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0)
            {
                Console.Error.WriteLine("自重启跳过：读不到当前进程路径");
                return;
            }

            Thread.Sleep(300);
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDir,
            };
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            try
            {
                Process.Start(info);
                Console.WriteLine("已拉起新实例（pid 见控制台）");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine("自重启失败：" + ex.Message);
            }
        }

        /// <summary>删文件——不存在 / 被占用都只出声，不抛。</summary>
        /// <param name="path">文件路径。</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("删除跳过：" + path + " :: " + ex.Message);
            }
        }
    }
}
