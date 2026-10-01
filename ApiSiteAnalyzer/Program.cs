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
using ApiSiteAnalyzer.Web;

namespace ApiSiteAnalyzer;

/// <summary>
/// 入口——API 站用量分析器。
/// 子命令：check（登录态）/ fetch（拉取入库）/ serve（面板，默认）/ stats（库内计数）/ sites（站点清单）。
/// </summary>
public static class Program
{
    /// <summary>默认配置文件路径（相对当前工作目录，缺失回落 exe 同级）。</summary>
    private const string DefaultConfigName = "config.json";

    /// <summary>入口。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>退出码。</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        string command = args.Length > 0 ? args[0] : "serve";

        // [段1] 选项与位置参数分开——`fetch --full` 里 --full 不是配置文件路径
        bool fullScan = false;
        string configArg = "";
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--full", StringComparison.OrdinalIgnoreCase))
            {
                fullScan = true;
            }
            else if (configArg.Length == 0)
            {
                configArg = args[i];
            }
        }

        string configPath = ResolveConfigPath(configArg);

        try
        {
            AppConfig config = ConfigLoader.Load(configPath);
            string dataDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "data");
            Directory.CreateDirectory(dataDir);

            List<IApiSite> sites = BuildSites(config, dataDir);
            string chromePath = ChromePath.Resolve(config.ChromePath);

            return command switch
            {
                "check" => await RunCheckAsync(sites, chromePath, dataDir, CancellationToken.None).ConfigureAwait(false),
                "fetch" => await RunFetchAsync(sites, chromePath, dataDir, !fullScan, CancellationToken.None).ConfigureAwait(false),
                "stats" => RunStats(sites, dataDir),
                "sites" => RunSites(sites),
                "serve" => await ServeRunner.RunAsync(config, sites, chromePath, dataDir, CancellationToken.None).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine("配置错误：" + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>解析配置文件路径——当前工作目录优先，缺失回落 exe 同级。</summary>
    private static string ResolveConfigPath(string given)
    {
        if (given.Length > 0)
        {
            return given;
        }

        if (File.Exists(DefaultConfigName))
        {
            return DefaultConfigName;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, DefaultConfigName);
        if (File.Exists(beside))
        {
            return beside;
        }

        throw new ConfigException("找不到配置文件 " + DefaultConfigName + "（当前工作目录与程序目录都没有）");
    }

    /// <summary>把配置转成站点适配器清单。</summary>
    private static List<IApiSite> BuildSites(AppConfig config, string dataDir)
    {
        // [段1] API key 存储（特批站点用）——落 data/keys.json，不入仓
        var keys = new KeyStore(Path.Combine(dataDir, "keys.json"));
        var sites = new List<IApiSite>();

        foreach (SiteConfig item in config.Sites)
        {
            string profileDir = item.ProfileDir.Length > 0
                ? item.ProfileDir
                : Path.Combine(dataDir, "profiles", item.Id);

            var site = new NewApiSite(item.Id, item.DisplayName, item.BaseUrl, profileDir, item.QuotaPerUnit, item.CurrencySymbol);

            // [段2] 有 key 即走 API key 直连通道（特批——仅该站）；没有 key 仍走浏览器通道
            site.ApiKey = keys.Get(item.Id);
            sites.Add(site);
        }

        return sites;
    }

    /// <summary>探测各站登录态。</summary>
    private static async Task<int> RunCheckAsync(List<IApiSite> sites, string chromePath, string dataDir, CancellationToken ct)
    {
        var hub = new BrowserHub(chromePath, Path.Combine(dataDir, "browsers.json"));
        var session = new SiteSession(hub);

        int notLoggedIn = 0;
        foreach (IApiSite site in sites)
        {
            LoginState state = await session.ProbeLoginAsync(site, ct).ConfigureAwait(false);
            if (state.LoggedIn)
            {
                Console.WriteLine("[已登录] " + site.DisplayName + " · 用户 " + state.Username +
                    " · 余额 " + state.Quota + " · 已用 " + state.UsedQuota + " · 请求数 " + state.RequestCount);
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
            Console.WriteLine("  真实余额：" + summary.Balance + " quota = " +
                (summary.Balance / site.QuotaPerUnit).ToString("0.0000") + " " + site.CurrencySymbol);

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

    /// <summary>打印库内计数。</summary>
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
    private static int RunSites(List<IApiSite> sites)
    {
        foreach (IApiSite site in sites)
        {
            Console.WriteLine(site.Id + " | " + site.DisplayName + " | " + site.BaseUrl +
                " | 用户目录 " + site.ProfileDir + " | 1 单位 = " + site.QuotaPerUnit + " quota");
        }

        return 0;
    }

    /// <summary>未知子命令——出声并非零退出。</summary>
    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine("未知子命令：" + command);
        Console.Error.WriteLine("可用：check / fetch / serve / stats / sites");
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
