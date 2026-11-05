using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using ApiSiteAnalyzer.Config;
using ApiSiteAnalyzer.Contract;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace ApiSiteAnalyzer
{
    /// <summary>
    /// 业务核心——实现 <see cref="IShellCore"/>，持有全部业务对象（配置 / 站点 / Kestrel / 后台采集）。
    /// <para>
    /// 生命周期：<c>new</c> → <c>Load</c>（读配置，一次）→ <c>Start</c>（起服务）→ <c>Stop</c>（停服务）。
    /// 换核 = 宿主丢弃本实例引用 → ALC 卸载 → 新 ALC 里 new 一份 → Load → Start。
    /// </para>
    /// </summary>
    public sealed class ShellCore : IShellCore
    {
        /// <summary>默认配置文件文件名。</summary>
        private const string DefaultConfigName = "config.json";

        /// <summary>配置（Load 后非空）。</summary>
        private AppConfig? _config;

        /// <summary>站点适配器清单。</summary>
        private List<IApiSite> _sites = new List<IApiSite>();

        /// <summary>数据目录（config 同级 data）。</summary>
        private string _dataDir = "";

        /// <summary>配置目录（config.json 所在目录——serve 自举据此定位 apis/）。</summary>
        private string _configDir = "";

        /// <summary>serve 自举结论（启动时写入；进核心快照，失败在快照里一眼可见）。</summary>
        private string _serveNote = "";

        /// <summary>程序目录（宿主传入）。</summary>
        private string _baseDir = "";

        /// <summary>chrome.exe 路径。</summary>
        private string _chromePath = "";

        /// <summary>Kestrel 应用（Start 后非空）。</summary>
        private WebApplication? _app;

        /// <summary>后台任务取消令牌。</summary>
        private CancellationTokenSource? _cts;

        /// <summary>启动时刻（快照用）。</summary>
        private DateTime _startedAt;

        /// <summary>核心版本（Core 程序集版本）。</summary>
        public string Version
        {
            get
            {
                var assembly = typeof(ShellCore).Assembly;
                string? info = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Length > 0
                    ? ((System.Reflection.AssemblyInformationalVersionAttribute)assembly
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)[0])
                        .InformationalVersion
                    : null;
                if (!string.IsNullOrEmpty(info))
                {
                    return info!;
                }

                return assembly.GetName().Version?.ToString() ?? "未知";
            }
        }

        /// <summary>
        /// 装载配置与站点——部署期声明只读一次（<c>config.json</c> 不因换核变成热读面）。
        /// </summary>
        /// <param name="configPath">配置文件路径（空 = 当前目录 → 程序目录）。</param>
        /// <param name="baseDir">程序目录。</param>
        public void Load(string configPath, string baseDir)
        {
            _baseDir = baseDir.Length > 0 ? baseDir : AppContext.BaseDirectory;
            string resolved = ResolveConfigPath(configPath);
            AppConfig config = ConfigLoader.Load(resolved);
            _config = config;

            string configDir = Path.GetDirectoryName(Path.GetFullPath(resolved)) ?? ".";
            _dataDir = Path.Combine(configDir, "data");
            _configDir = configDir;
            Directory.CreateDirectory(_dataDir);

            _sites = CliCommands.BuildSites(config, _dataDir);
            _chromePath = ChromePath.Resolve(config.ChromePath);
        }

        /// <summary>启动服务——Kestrel 绑定 + 后台采集任务。</summary>
        /// <param name="openBrowser">true = 启动后打开浏览器（仅首次启动）。</param>
        public void Start(bool openBrowser)
        {
            if (_app != null)
            {
                throw new InvalidOperationException("核心已启动——先 Stop 再 Start");
            }

            if (_config == null)
            {
                throw new InvalidOperationException("核心未 Load——先 Load 再 Start");
            }

            string runDir = Path.Combine(_dataDir, "run");
            Directory.CreateDirectory(runDir);
            _cts = new CancellationTokenSource();
            _startedAt = DateTime.Now;

            WebApplication app = ServeRunner.CreateApp(
                _config,
                _sites,
                _chromePath,
                _dataDir,
                _cts.Token,
                openBrowser,
                Path.Combine(runDir, "reload.request"),
                Path.Combine(runDir, "service.token"));
            app.Start();
            _app = app;

            // [段1b] 本机 serve 自举——只起 apis 一个进程即全开（中转站 iframe 与第四站取数都挂在 18100）。
            //        已在跑则不动；拉起失败只出声，不影响面板本身启动。
            try
            {
                _serveNote = ServeLauncher.EnsureUp(_sites, _configDir, runDir);
            }
            catch (Exception ex)
            {
                _serveNote = "serve 自举异常：" + ex.GetType().Name + ": " + ex.Message;
                Console.Error.WriteLine(_serveNote);
            }
        }

        /// <summary>停止服务（幂等）——取消后台任务 + 优雅停 Kestrel。</summary>
        public void Stop()
        {
            CancellationTokenSource? cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException ex)
                {
                    Console.Error.WriteLine("核心停止告警（令牌已释放）：" + ex.Message);
                }
            }

            WebApplication? app = _app;
            _app = null;
            if (app != null)
            {
                try
                {
                    app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException
                    || ex is AggregateException || ex is IOException)
                {
                    Console.Error.WriteLine("核心停止告警：" + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    (app as IDisposable)?.Dispose();
                }
            }

            if (cts != null)
            {
                cts.Dispose();
            }
        }

        /// <summary>核心状态快照（诊断面）。</summary>
        /// <returns>JSON 对象字符串。</returns>
        public string SnapshotJson()
        {
            bool running = _app != null;
            int uptime = _startedAt == default ? 0 : (int)Math.Max(0, (DateTime.Now - _startedAt).TotalSeconds);
            var payload = new
            {
                version = Version,
                pid = Environment.ProcessId,
                port = _config == null ? 0 : _config.Port,
                sites = _sites.Count,
                dataDir = _dataDir,
                serve = _serveNote,
                running,
                startedAt = _startedAt == default ? "" : _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                uptimeSeconds = uptime,
            };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>跑一条 CLI 子命令。</summary>
        /// <param name="verb">子命令名。</param>
        /// <param name="optionsJson">选项 JSON（<c>{"full":true,"site":"xyjun"}</c>）。</param>
        /// <returns>退出码。</returns>
        public int RunCommand(string verb, string optionsJson)
        {
            if (_config == null)
            {
                Console.Error.WriteLine("核心未 Load");
                return 1;
            }

            bool full = false;
            string siteFilter = "";
            if (!string.IsNullOrEmpty(optionsJson))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(optionsJson);
                    if (doc.RootElement.TryGetProperty("full", out JsonElement f))
                    {
                        full = f.ValueKind == JsonValueKind.True;
                    }

                    if (doc.RootElement.TryGetProperty("site", out JsonElement s))
                    {
                        siteFilter = s.GetString() ?? "";
                    }
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine("选项解析失败：" + ex.Message);
                    return 2;
                }
            }

            return CliCommands.Run(verb, _config, _sites, _chromePath, _dataDir, full, siteFilter);
        }

        /// <summary>释放（停服务）。</summary>
        public void Dispose()
        {
            Stop();
        }

        /// <summary>解析配置文件路径——当前工作目录优先，缺失回落程序目录。</summary>
        /// <param name="given">命令行给的路径（空 = 自动）。</param>
        /// <returns>配置文件路径。</returns>
        private string ResolveConfigPath(string given)
        {
            if (given.Length > 0)
            {
                return given;
            }

            if (File.Exists(DefaultConfigName))
            {
                return DefaultConfigName;
            }

            string beside = Path.Combine(_baseDir, DefaultConfigName);
            if (File.Exists(beside))
            {
                return beside;
            }

            throw new ConfigException("找不到配置文件 " + DefaultConfigName + "（当前工作目录与程序目录都没有）");
        }
    }
}
