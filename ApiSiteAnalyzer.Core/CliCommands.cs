using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ApiSiteAnalyzer.Browser;
using ApiSiteAnalyzer.Collect;
using ApiSiteAnalyzer.Config;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;

namespace ApiSiteAnalyzer
{
    /// <summary>
    /// CLI 子命令实现——check / fetch / stats / sites / probe。
    /// <para>
    /// 归属 Core：这些命令直用 ConfigLoader / Db / Sites 等业务类型，按三层铁律不能留在宿主（宿主不得静态引用 Core）。
    /// 宿主只做参数解析与转发（<c>IShellCore.RunCommand</c>）。
    /// </para>
    /// </summary>
    internal static class CliCommands
    {
        /// <summary>跑一条子命令。</summary>
        /// <param name="command">子命令名。</param>
        /// <param name="config">配置。</param>
        /// <param name="sites">站点清单（全量）。</param>
        /// <param name="chromePath">chrome.exe 路径。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <param name="fullScan">fetch 全量重扫。</param>
        /// <param name="siteFilter">单站过滤（空 = 全站）。</param>
        /// <returns>退出码。</returns>
        public static int Run(string command, AppConfig config, List<IApiSite> sites, string chromePath,
            string dataDir, bool fullScan, string siteFilter)
        {
            // [段1b] --site=<站点键> 过滤——单站跑 check / fetch / stats 时免跑全站（站多时省事）；未命中即出声退出
            if (siteFilter.Length > 0)
            {
                var filtered = new List<IApiSite>();
                foreach (IApiSite candidate in sites)
                {
                    if (string.Equals(candidate.Id, siteFilter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.DisplayName, siteFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        filtered.Add(candidate);
                    }
                }

                if (filtered.Count == 0)
                {
                    Console.Error.WriteLine("未找到站点：" + siteFilter);
                    Console.Error.WriteLine("可用：" + string.Join(" / ", sites.ConvertAll(s => s.Id)));
                    return 2;
                }

                sites = filtered;
            }

            return command switch
            {
                "check" => RunCheckAsync(sites, chromePath, dataDir, CancellationToken.None).GetAwaiter().GetResult(),
                "fetch" => RunFetchAsync(sites, chromePath, dataDir, !fullScan, CancellationToken.None).GetAwaiter().GetResult(),
                "stats" => RunStats(sites, dataDir),
                "sites" => RunSites(sites),
                "probe" => RunProbeAsync(sites, chromePath, dataDir, CancellationToken.None).GetAwaiter().GetResult(),
                "serve" => ServeNotHere(),
                _ => UnknownCommand(command),
            };
        }

        /// <summary>把配置转成站点适配器清单。</summary>
        /// <param name="config">配置。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <returns>站点清单。</returns>
        public static List<IApiSite> BuildSites(AppConfig config, string dataDir)
        {
            // [段1] API key 存储（特批站点用）——落 data/keys.json，不入仓
            var keys = new KeyStore(Path.Combine(dataDir, "keys.json"));
            var sites = new List<IApiSite>();

            foreach (SiteConfig item in config.Sites)
            {
                string profileDir = item.ProfileDir.Length > 0
                    ? item.ProfileDir
                    : Path.Combine(dataDir, "profiles", item.Id);

                var site = BuildSite(item, profileDir);

                // [段2] 有 key 即走 API key 直连通道（特批——仅该站）；没有 key 仍走浏览器通道
                if (site is IDirectSite direct)
                {
                    direct.ApiKey = keys.Get(item.Id);
                }

                sites.Add(site);
            }

            return sites;
        }

        /// <summary>按配置里的 type 造对应适配器（新增站点类型时在此登记）。</summary>
        /// <param name="item">站点配置。</param>
        /// <param name="profileDir">浏览器用户目录。</param>
        /// <returns>适配器。</returns>
        private static IApiSite BuildSite(SiteConfig item, string profileDir)
        {
            if (string.Equals(item.Type, "buddy", StringComparison.OrdinalIgnoreCase))
            {
                return new BuddySite(item.Id, item.DisplayName, item.BaseUrl, profileDir, item.QuotaPerUnit, item.CurrencySymbol);
            }

            if (string.Equals(item.Type, "sub2api", StringComparison.OrdinalIgnoreCase))
            {
                return new Sub2ApiSite(item.Id, item.DisplayName, item.BaseUrl, profileDir, item.QuotaPerUnit, item.CurrencySymbol);
            }

            if (string.Equals(item.Type, "workserve", StringComparison.OrdinalIgnoreCase))
            {
                return new WorkServeSite(item.Id, item.DisplayName, item.BaseUrl, profileDir, item.QuotaPerUnit, item.CurrencySymbol);
            }

            return new NewApiSite(item.Id, item.DisplayName, item.BaseUrl, profileDir, item.QuotaPerUnit, item.CurrencySymbol);
        }

        /// <summary>余额位取值——台账口径的站点（BalanceFromLedger，如知序空间「已用积分」）取本地累计消费，其余取探针值。</summary>
        /// <param name="site">站点适配器。</param>
        /// <param name="dataDir">数据目录（库所在）。</param>
        /// <param name="probeQuota">探针报回的余额位（quota 单位）。</param>
        /// <returns>应展示的余额位（quota 单位）。</returns>
        private static long BalanceQuota(IApiSite site, string dataDir, long probeQuota)
        {
            if (!site.BalanceFromLedger)
            {
                return probeQuota;
            }

            using var db = new Db(Path.Combine(dataDir, "usage.db"));
            return db.SumQuota(site.Id);
        }

        /// <summary>探测各站登录态。</summary>
        /// <param name="sites">站点清单。</param>
        /// <param name="chromePath">chrome.exe 路径。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>退出码。</returns>
        private static async Task<int> RunCheckAsync(List<IApiSite> sites, string chromePath, string dataDir, CancellationToken ct)
        {
            var hub = new BrowserHub(chromePath, Path.Combine(dataDir, "browsers.json"));
            hub.Silent = ReadSilentSetting(dataDir);
            hub.KeepWindow = ReadKeepWindowSetting(dataDir);
            var session = new SiteSession(hub);

            int notLoggedIn = 0;
            foreach (IApiSite site in sites)
            {
                LoginState state = await session.ProbeLoginAsync(site, ct).ConfigureAwait(false);
                if (state.LoggedIn)
                {
                    /* 余额位按站点自报的口径打印——货币站折成金额（8 位小数），自报单位的站（如积分）除回该单位 */
                    string balanceText;
                    if (site.BalanceUnit.Length > 0)
                    {
                        double scale = site.BalanceScale > 0 ? site.BalanceScale : 1;
                        balanceText = (BalanceQuota(site, dataDir, state.Quota) / scale).ToString("0.##") + " " + site.BalanceUnit;
                    }
                    else
                    {
                        balanceText = (BalanceQuota(site, dataDir, state.Quota) / site.QuotaPerUnit).ToString("0.00000000") + " " + site.CurrencySymbol;
                    }

                    Console.WriteLine("[已登录] " + site.DisplayName + " · 用户 " + state.Username +
                        " · " + site.BalanceLabel + " " + balanceText +
                        " · 已用 " + state.UsedQuota + " · 请求数 " + state.RequestCount);
                }
                else
                {
                    notLoggedIn++;
                    Console.WriteLine("[未登录] " + site.DisplayName + " — " + state.Message);
                    Console.WriteLine("         登录入口：" + site.LoginUrl + "（用户目录 " + site.ProfileDir + "）");
                }
            }

            return notLoggedIn > 0 ? 3 : 0;
        }

        /// <summary>拉取各站用量并入库。</summary>
        /// <param name="sites">站点清单。</param>
        /// <param name="chromePath">chrome.exe 路径。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <param name="incremental">是否增量（追平历史即停）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>退出码。</returns>
        private static async Task<int> RunFetchAsync(List<IApiSite> sites, string chromePath, string dataDir, bool incremental, CancellationToken ct)
        {
            var hub = new BrowserHub(chromePath, Path.Combine(dataDir, "browsers.json"));
            hub.Silent = ReadSilentSetting(dataDir);
            hub.KeepWindow = ReadKeepWindowSetting(dataDir);
            var session = new SiteSession(hub);

            using var db = new Db(Path.Combine(dataDir, "usage.db"));
            int failed = 0;

            foreach (IApiSite site in sites)
            {
                Console.WriteLine("== " + site.DisplayName + " ==");

                FetchSummary summary = await session.FetchAllAsync(
                    site,
                    100,
                    new FetchOptions { Incremental = incremental },
                    (page, result) => PageWriter.Write(db, site, result),
                    text => Console.WriteLine("  " + text),
                    ct).ConfigureAwait(false);

                if (!summary.Ok)
                {
                    failed++;
                    Console.WriteLine("  中止：" + summary.Error);
                    if (summary.NotLoggedIn)
                    {
                        Console.WriteLine("  登录入口：" + site.LoginUrl);
                    }
                    continue;
                }

                long rows = db.Count(site.Id);
                db.MarkFetched(site.Id, rows);
                Console.WriteLine("  完成：拉取 " + summary.Pages + " 页 / " + summary.Fetched + " 条 · 新增 " + summary.Added +
                    " 条 · 库内 " + rows + " 行" + (summary.StoppedEarly ? " · 已追平历史（提前停止）" : ""));

                /* 余额位按站点自报的口径打印——货币站折成金额，自报单位的站（如积分）除回该单位 */
                if (site.BalanceUnit.Length > 0)
                {
                    double scale = site.BalanceScale > 0 ? site.BalanceScale : 1;
                    Console.WriteLine("  " + site.BalanceLabel + "：" + (BalanceQuota(site, dataDir, summary.Balance) / scale).ToString("0.##") + " " + site.BalanceUnit);
                }
                else
                {
                    long shownQuota = BalanceQuota(site, dataDir, summary.Balance);
                    Console.WriteLine("  " + site.BalanceLabel + "：" + shownQuota + " quota = " +
                        (shownQuota / site.QuotaPerUnit).ToString("0.0000") + " " + site.CurrencySymbol);
                }

                if (summary.Snapshot is not null)
                {
                    db.SaveSnapshot(site.Id, summary.Snapshot);
                    Console.WriteLine("  站点口径：" + summary.Snapshot.TotalCount + " 次 / " +
                        (summary.Snapshot.TotalQuota / site.QuotaPerUnit).ToString("0.0000") + " " + site.CurrencySymbol +
                        " / " + summary.Snapshot.TotalTokens + " tokens");
                }
                else
                {
                    Console.WriteLine("  站点口径：未取到（不阻断主流程）");
                }
            }

            return failed > 0 ? 1 : 0;
        }

        /// <summary>
        /// 侦察——新站点接入时取原始样本（登录探针 / 首页两档页大小 / 站点口径），落盘供校准适配器口径。
        /// **强制可见窗口**：未登录时导航到登录页，登录由人完成（程序不代填账号密码）。
        /// </summary>
        /// <param name="sites">站点清单。</param>
        /// <param name="chromePath">chrome.exe 路径。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>退出码。</returns>
        private static async Task<int> RunProbeAsync(List<IApiSite> sites, string chromePath, string dataDir, CancellationToken ct)
        {
            List<IApiSite> targets = sites;

            var hub = new BrowserHub(chromePath, Path.Combine(dataDir, "browsers.json"));
            hub.Silent = ReadSilentSetting(dataDir);
            hub.KeepWindow = ReadKeepWindowSetting(dataDir);
            var session = new SiteSession(hub);

            string outDir = Path.Combine(dataDir, "probe");
            Directory.CreateDirectory(outDir);

            int failed = 0;
            foreach (IApiSite site in targets)
            {
                Console.WriteLine("== " + site.DisplayName + "（" + site.Id + "） ==");
                ProbeResult result = await session.ProbeAsync(site, 100, 200, true, ct).ConfigureAwait(false);
                if (result.Error.Length > 0)
                {
                    failed++;
                    Console.WriteLine("  侦察失败：" + result.Error);
                    continue;
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(outDir, site.Id + "-" + stamp + ".txt");
                string recordPath = Path.Combine(outDir, site.Id + "-" + stamp + "-record.json");
                var text = new StringBuilder();
                text.AppendLine("site=" + site.Id + " url=" + result.Url);
                text.AppendLine("== login ==");
                text.AppendLine(result.LoginRaw);
                text.AppendLine("== usage(100) ==");
                text.AppendLine(result.UsageRaw);
                text.AppendLine("== usage(200) ==");
                text.AppendLine(result.UsageWideRaw);
                text.AppendLine("== snapshot ==");
                text.AppendLine(result.SnapshotRaw);
                text.AppendLine("== endpoints ==");
                text.AppendLine(result.EndpointRaw);
                File.WriteAllText(path, text.ToString(), Encoding.UTF8);

                // [段2] 首页条数对照 + 首条记录单写——整页样本数百 KB，单条才便于逐字段校准
                string shapes = DescribePages(site, result.UsageRaw, result.UsageWideRaw, recordPath);

                Console.WriteLine("  页面：" + result.Url);
                Console.WriteLine("  样本落盘：" + path);
                Console.WriteLine("  首条记录：" + recordPath);
                Console.WriteLine("  登录探针：" + Short(result.LoginRaw));
                Console.WriteLine("  分页实况：" + shapes);
                Console.WriteLine("  站点口径：" + Short(result.SnapshotRaw));
                Console.WriteLine("  端点普查：");
                foreach (string line in DescribeEndpoints(result.EndpointRaw))
                {
                    Console.WriteLine("    " + line);
                }
            }

            return failed > 0 ? 1 : 0;
        }

        /// <summary>把端点普查结果压成一行一端点（路径 / 状态 / 长度 / 开头片段）。</summary>
        /// <param name="raw">普查原文。</param>
        /// <returns>逐行文本。</returns>
        private static List<string> DescribeEndpoints(string raw)
        {
            var lines = new List<string>();
            if (raw.Length == 0)
            {
                return lines;
            }

            try
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(raw);
                foreach (System.Text.Json.JsonElement item in doc.RootElement.EnumerateArray())
                {
                    string path = item.TryGetProperty("path", out System.Text.Json.JsonElement p) ? p.GetString() ?? "" : "";
                    string status = item.TryGetProperty("status", out System.Text.Json.JsonElement s) ? s.ToString() : "?";
                    string length = item.TryGetProperty("length", out System.Text.Json.JsonElement l) ? l.ToString() : "0";
                    string head = item.TryGetProperty("head", out System.Text.Json.JsonElement h) ? (h.GetString() ?? "") : "";
                    string error = item.TryGetProperty("error", out System.Text.Json.JsonElement e) ? (e.GetString() ?? "") : "";
                    lines.Add(status + " · " + length + "B · " + path +
                        " · " + (error.Length > 0 ? error : Short(head)));
                }
            }
            catch (Exception ex)
            {
                lines.Add("普查结果不可解析：" + ex.Message);
            }

            return lines;
        }

        /// <summary>
        /// 分页实况——两档页大小各返回多少条 + 服务端 total（**判站点是否认页大小**：认则两档条数不同）。
        /// 顺带把**首条记录原文**单写一份小样本（整页样本动辄数百 KB，单条才便于逐字段校准）。
        /// </summary>
        /// <param name="site">站点。</param>
        /// <param name="rawA">100 档原文。</param>
        /// <param name="rawB">200 档原文。</param>
        /// <param name="recordPath">首条记录落盘路径。</param>
        /// <returns>一行摘要。</returns>
        private static string DescribePages(IApiSite site, string rawA, string rawB, string recordPath)
        {
            var parts = new List<string>();
            parts.Add(Sample(site, "page_size=100", rawA, recordPath));
            parts.Add(Sample(site, "page_size=200", rawB, ""));
            return string.Join(" · ", parts);
        }

        /// <summary>解析一档页大小的响应——回「条数 / total」；给了样本路径则把首条原文写盘。</summary>
        /// <param name="site">站点。</param>
        /// <param name="label">档位标签。</param>
        /// <param name="raw">响应原文。</param>
        /// <param name="recordPath">首条记录路径（空 = 不写）。</param>
        /// <returns>一行摘要。</returns>
        private static string Sample(IApiSite site, string label, string raw, string recordPath)
        {
            if (raw.Length == 0)
            {
                return label + "：无响应";
            }

            try
            {
                string body = ExtractBody(raw);
                if (body.Length == 0)
                {
                    return label + "：非 200（" + Short(raw) + "）";
                }

                UsagePage parsed = site.ParseUsagePage(body);
                if (parsed.Records.Count > 0 && recordPath.Length > 0)
                {
                    File.WriteAllText(recordPath, parsed.Records[0], Encoding.UTF8);
                }

                return label + "：条数 " + parsed.Records.Count + " / total " + (parsed.Total < 0 ? "无" : parsed.Total.ToString());
            }
            catch (Exception ex)
            {
                return label + "：解析失败（" + ex.Message + "）";
            }
        }

        /// <summary>从 `{"status":...,"body":"..."}` 外壳里取出 body 文本（非 200 返回空串）。</summary>
        /// <param name="raw">外壳原文。</param>
        /// <returns>body 文本。</returns>
        private static string ExtractBody(string raw)
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("status", out System.Text.Json.JsonElement status) || status.ToString() != "200")
            {
                return "";
            }

            return doc.RootElement.TryGetProperty("body", out System.Text.Json.JsonElement body) ? (body.GetString() ?? "") : "";
        }

        /// <summary>截一段样本用于控制台预览（完整样本已落盘）。</summary>
        /// <param name="raw">原文。</param>
        /// <returns>预览文本。</returns>
        private static string Short(string raw)
        {
            if (raw.Length <= 400)
            {
                return raw;
            }

            return raw.Substring(0, 400) + "…（共 " + raw.Length + " 字符）";
        }

        /// <summary>打印库内计数。</summary>
        /// <param name="sites">站点清单。</param>
        /// <param name="dataDir">数据目录。</param>
        /// <returns>退出码。</returns>
        private static int RunStats(List<IApiSite> sites, string dataDir)
        {
            using var db = new Db(Path.Combine(dataDir, "usage.db"));

            foreach (IApiSite site in sites)
            {
                long rows = db.Count(site.Id);
                string last = db.LastFetchAt(site.Id);
                Console.WriteLine(site.DisplayName + "（" + site.Id + "）：" + rows + " 行 · 上次拉取 " +
                    (last.Length == 0 ? "从未" : last));
            }

            return 0;
        }

        /// <summary>打印站点清单。</summary>
        /// <param name="sites">站点清单。</param>
        /// <returns>退出码。</returns>
        private static int RunSites(List<IApiSite> sites)
        {
            foreach (IApiSite site in sites)
            {
                Console.WriteLine(site.Id + " | " + site.DisplayName + " | " + site.BaseUrl +
                    " | 用户目录 " + site.ProfileDir + " | 1 单位 = " + site.QuotaPerUnit + " quota");
            }

            return 0;
        }

        /// <summary>读面板设置里的静默采集开关（无设置文件 = 默认静默）——CLI 与面板走同一份设置。</summary>
        /// <param name="dataDir">数据目录。</param>
        /// <returns>是否静默（headless）。</returns>
        private static bool ReadSilentSetting(string dataDir)
        {
            var settings = new ApiSiteAnalyzer.Web.PanelSettings(Path.Combine(dataDir, "settings.json"));
            return settings.Silent;
        }

        /// <summary>读面板设置里的「登录窗口保活」开关（无设置文件 = 默认开）——CLI 与面板走同一份设置。</summary>
        /// <param name="dataDir">数据目录。</param>
        /// <returns>是否保活。</returns>
        private static bool ReadKeepWindowSetting(string dataDir)
        {
            var settings = new ApiSiteAnalyzer.Web.PanelSettings(Path.Combine(dataDir, "settings.json"));
            return settings.KeepLoginWindow;
        }

        /// <summary>未知子命令——出声并非零退出。</summary>
        /// <param name="command">子命令名。</param>
        /// <returns>退出码 2。</returns>
        private static int UnknownCommand(string command)
        {
            Console.Error.WriteLine("未知子命令：" + command);
            Console.Error.WriteLine("可用：check / fetch / serve / stats / sites / probe");
            Console.Error.WriteLine("选项：--full（fetch 全量重扫）· --site=<站点键>（只跑该站）");
            return 2;
        }

        /// <summary>serve 由宿主主路径承接——CLI 通道不跑常驻服务。</summary>
        /// <returns>退出码 2。</returns>
        private static int ServeNotHere()
        {
            Console.Error.WriteLine("serve 由宿主主路径承接（不经 CLI 通道）");
            return 2;
        }
    }

    /// <summary>chrome 路径解析——配置优先，空则按常见安装位置探测。</summary>
    public static class ChromePath
    {
        /// <summary>解析 chrome.exe 路径。</summary>
        /// <param name="configured">配置里指定的路径（空 = 自动探测）。</param>
        /// <returns>可执行文件路径。</returns>
        public static string Resolve(string configured)
        {
            if (configured.Length > 0)
            {
                if (!File.Exists(configured))
                {
                    throw new ConfigException("配置的 chromePath 不存在：" + configured);
                }

                return configured;
            }

            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            };

            foreach (string path in candidates)
            {
                if (path.Length > 0 && File.Exists(path))
                {
                    return path;
                }
            }

            throw new ConfigException("未找到 chrome.exe（在 config.json 里显式指定 chromePath）");
        }
    }
}
