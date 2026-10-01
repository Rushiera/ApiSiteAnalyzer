using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ApiSiteAnalyzer.Browser;
using ApiSiteAnalyzer.Collect;
using ApiSiteAnalyzer.Config;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 面板宿主——Kestrel + Minimal API + 内嵌单页。
/// 采集 / 探测等重操作走同一条串行队列，同一时刻只跑一个浏览器操作。
/// </summary>
public static class ServeRunner
{
    /// <summary>内嵌单页路径（程序集内资源名）。</summary>
    private const string IndexResource = "ApiSiteAnalyzer.Web.wwwroot.index.html";

    /// <summary>启动面板。</summary>
    /// <param name="config">配置。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="dataDir">数据目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退出码。</returns>
    public static async Task<int> RunAsync(AppConfig config, List<IApiSite> sites, string chromePath, string dataDir, CancellationToken ct)
    {
        var hub = new BrowserHub(chromePath, Path.Combine(dataDir, "browsers.json"));
        var session = new SiteSession(hub);
        var queue = new TaskQueue();
        var dbPath = Path.Combine(dataDir, "usage.db");
        var state = new PanelState(sites, dbPath);
        var settings = new PanelSettings(Path.Combine(dataDir, "settings.json"));
        var keys = new KeyStore(Path.Combine(dataDir, "keys.json"));
        var collector = new AutoCollector(session, sites, dbPath, state, settings);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:" + config.Port);
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();
        string token = Guid.NewGuid().ToString("N").Substring(0, 16);

        // [段1] 单页
        app.MapGet("/", () => Results.Content(ReadIndex(), "text/html; charset=utf-8"));

        // [段2] 站点清单 + 库内计数（面板首屏）
        app.MapGet("/api/sites", () =>
        {
            using var db = new Db(dbPath);
            var list = new List<object>();
            foreach (IApiSite site in sites)
            {
                SiteSnapshot? snapshot = db.ReadSnapshot(site.Id);

                // [段2a] 通道自证——有 key 即走 API key 直连（特批站点）；key 明文回传供面板顶部编辑
                string apiKey = site is NewApiSite direct ? direct.ApiKey : "";
                list.Add(new
                {
                    id = site.Id,
                    displayName = site.DisplayName,
                    baseUrl = site.BaseUrl,
                    channel = apiKey.Length > 0 ? "apikey" : "browser",
                    apiKey = apiKey,
                    loginUrl = site.LoginUrl,
                    profileDir = site.ProfileDir,
                    quotaPerUnit = site.QuotaPerUnit,
                    currencySymbol = site.CurrencySymbol,
                    rows = db.Count(site.Id),
                    lastFetchAt = db.LastFetchAt(site.Id),
                    snapshot = snapshot is null ? null : new
                    {
                        fetchedAt = snapshot.FetchedAt,
                        totalCount = snapshot.TotalCount,
                        totalQuota = snapshot.TotalQuota,
                        totalAmount = snapshot.TotalQuota / (site.QuotaPerUnit <= 0 ? 500000 : site.QuotaPerUnit),
                        totalTokens = snapshot.TotalTokens,
                        avgRpm = snapshot.AvgRpm,
                        avgTpm = snapshot.AvgTpm,
                    },
                });
            }

            // 版本自证——「跑的是哪份产物」从口头核对变成一眼可见的实况
            string running = AppVersion();
            (string Version, string FileName)? newest = FindNewestSlot();
            string newestVersion = newest is null ? "" : newest.Value.Version;

            return Results.Json(new
            {
                ok = true,
                version = running,
                exePath = Environment.ProcessPath ?? "",
                newestVersion = newestVersion,
                newestExe = newest is null ? "" : newest.Value.FileName,
                hasUpdate = IsNewer(newestVersion, running),
                sites = list,
                running = state.Running,
                settings = new
                {
                    autoEnabled = settings.AutoEnabled,
                    autoIntervalSeconds = settings.AutoIntervalSeconds,
                    incremental = settings.Incremental,
                    minIntervalSeconds = PanelSettings.MinIntervalSeconds,
                    maxIntervalSeconds = PanelSettings.MaxIntervalSeconds,
                },
            });
        });

        // [段2b] 保存面板设置（自动采集开关 + 间隔）——落 data/settings.json
        //        入口面零容忍：缺值 / 非法值一律报错，不静默回落默认值
        app.MapPost("/api/settings", async (HttpContext context) =>
        {
            string auto = await ReadFieldAsync(context, "auto");
            string interval = await ReadFieldAsync(context, "interval");
            string incremental = await ReadFieldAsync(context, "incremental");

            if (auto != "true" && auto != "false")
            {
                return Results.Json(new { ok = false, error = "auto 必须是 true / false，收到：" + auto });
            }

            if (incremental != "true" && incremental != "false")
            {
                return Results.Json(new { ok = false, error = "incremental 必须是 true / false，收到：" + incremental });
            }

            if (!int.TryParse(interval, out int seconds))
            {
                return Results.Json(new { ok = false, error = "interval 必须是整数秒，收到：" + interval });
            }

            settings.Update(auto == "true", seconds, incremental == "true");
            collector.Reset();
            return Results.Json(new
            {
                ok = true,
                autoEnabled = settings.AutoEnabled,
                autoIntervalSeconds = settings.AutoIntervalSeconds,
                incremental = settings.Incremental,
            });
        });

        // [段3] 登录态探测——未登录时面板提示「去登录」
        app.MapPost("/api/login-check", async (HttpContext context) =>
        {
            string siteId = await ReadFieldAsync(context, "site");
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            try
            {
                LoginState login = await queue.RunAsync(() => session.ProbeLoginAsync(site, CancellationToken.None)).ConfigureAwait(false);
                return Results.Json(new
                {
                    ok = true,
                    loggedIn = login.LoggedIn,
                    username = login.Username,
                    quota = login.Quota,
                    usedQuota = login.UsedQuota,
                    requestCount = login.RequestCount,
                    group = login.Group,
                    message = login.Message,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // [段4] 打开登录窗口——受控实例，登录页在通道标签页里打开（钩子已就位，登录态留在页面内）
        app.MapPost("/api/open-login", async (HttpContext context) =>
        {
            string siteId = await ReadFieldAsync(context, "site");
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            try
            {
                BrowserInstance instance = await queue.RunAsync(() =>
                    session.OpenLoginAsync(site, CancellationToken.None)).ConfigureAwait(false);

                return Results.Json(new
                {
                    ok = true,
                    port = instance.Port,
                    message = "已打开浏览器窗口并切到登录页——登录完成后回到面板点「检查登录」",
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // [段4b] 保存 API key（**特批**——仅 47.250.160.225:8443 一站）——落 data/keys.json，立即生效并回执探活结果
        app.MapPost("/api/save-key", async (HttpContext context) =>
        {
            string siteId = await ReadFieldAsync(context, "site");
            string key = (await ReadFieldAsync(context, "key")).Trim();
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            if (site is not NewApiSite direct)
            {
                return Results.Json(new { ok = false, error = "该站点走浏览器登录通道，不接受 API key" });
            }

            if (key.Length == 0)
            {
                return Results.Json(new { ok = false, error = "API key 不能为空" });
            }

            direct.ApiKey = key;
            keys.Set(siteId, key);

            try
            {
                LoginState probe = await queue.RunAsync(() => session.ProbeLoginAsync(direct, CancellationToken.None)).ConfigureAwait(false);
                return Results.Json(new
                {
                    ok = true,
                    saved = true,
                    probeOk = probe.LoggedIn,
                    username = probe.Username,
                    requestCount = probe.RequestCount,
                    message = probe.LoggedIn
                        ? "已保存并生效——key 有效，本次可见 " + probe.RequestCount + " 条记录"
                        : "已保存，但探活失败：" + probe.Message,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = true, saved = true, probeOk = false, message = "已保存，但探活异常：" + ex.Message });
            }
        });

        // [段5] 拉取——入队，后台跑；面板轮询 /api/progress
        //        full=true 为全量重扫（不追平提前停止）；默认走增量（站点列表新的在前，追平即停）
        app.MapPost("/api/fetch", async (HttpContext context) =>
        {
            string siteId = await ReadFieldAsync(context, "site");
            string full = await ReadFieldAsync(context, "full");
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            bool incremental = full != "true" && settings.Incremental;
            if (!state.TryBegin(site.DisplayName))
            {
                return Results.Json(new { ok = false, error = "已有采集在进行中" });
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchSiteAsync(session, site, dbPath, state, incremental).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    state.Fail(ex.Message);
                }
            });

            return Results.Json(new
            {
                ok = true,
                incremental = incremental,
                message = incremental ? "已开始增量采集（追平历史即停）" : "已开始全量采集",
            });
        });

        // [段6] 进度
        app.MapGet("/api/progress", () => Results.Json(state.Snapshot()));

        // [段7] 分析面
        app.MapGet("/api/analysis", (HttpContext context) =>
        {
            string siteId = context.Request.Query["site"].ToString();
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            string range = context.Request.Query["range"].ToString();
            if (range.Length == 0)
            {
                range = "consume";
            }

            int[] types = range switch
            {
                "all" => Array.Empty<int>(),
                "consume" => new[] { 2 },
                _ => new[] { 2 },
            };

            using var db = new Db(dbPath);
            OverviewStat overview = db.Overview(site.Id, types);
            List<AggregateRow> byModel = db.ByModel(site.Id, types, 30);
            List<AggregateRow> byDay = db.ByDay(site.Id, types);
            List<AggregateRow> byHour = db.ByHour(site.Id, types);
            List<AggregateRow> byToken = db.ByToken(site.Id, types, 20);
            List<AggregateRow> byGroup = db.ByGroup(site.Id, types, 20);
            List<UsageRecord> recent = db.Recent(site.Id, types, 50);

            double unit = site.QuotaPerUnit <= 0 ? 500000 : site.QuotaPerUnit;

            return Results.Json(new
            {
                ok = true,
                site = new { id = site.Id, displayName = site.DisplayName, currencySymbol = site.CurrencySymbol, quotaPerUnit = unit },
                overview = new
                {
                    count = overview.Count,
                    quota = overview.Quota,
                    amount = overview.Quota / unit,
                    promptTokens = overview.PromptTokens,
                    completionTokens = overview.CompletionTokens,
                    cacheTokens = overview.CacheTokens,
                    cacheHitRate = overview.PromptTokens > 0 ? (double)overview.CacheTokens / overview.PromptTokens : 0,
                    requestIdCount = overview.RequestIdCount,
                    recentAvgFirstTokenMs = overview.RecentAvgFirstTokenMs,
                    recentAvgUseTime = overview.RecentAvgUseTime,
                    recentAvgSpeedTps = overview.RecentAvgSpeedTps,
                    recentSampleCount = overview.RecentSampleCount,
                },
                byModel = byModel.Select(r => Map(r, unit)),
                byDay = byDay.Select(r => Map(r, unit)),
                byHour = byHour.Select(r => Map(r, unit)),
                byToken = byToken.Select(r => Map(r, unit)),
                byGroup = byGroup.Select(r => Map(r, unit)),
                recent = recent.Select(r => new
                {
                    id = r.RemoteId,
                    createdAt = r.CreatedAt,
                    type = r.Type,
                    modelName = r.ModelName,
                    tokenName = r.TokenName,
                    groupName = r.GroupName,
                    quota = r.Quota,
                    amount = r.Quota / unit,
                    promptTokens = r.PromptTokens,
                    completionTokens = r.CompletionTokens,
                    cacheTokens = r.CacheTokens,
                    useTime = r.UseTime,
                    firstTokenMs = r.FirstTokenMs,
                    speedTps = r.SpeedTps,
                    isStream = r.IsStream,
                    requestId = r.RequestId,
                    upstreamModel = r.UpstreamModel,
                    content = r.Content,
                }),
            });
        });

        // [段7b] 已探明请求 ID 明细——卡片点开的逐条清单（去重后按时刻倒序分页）
        app.MapGet("/api/request-ids", (HttpContext context) =>
        {
            string siteId = context.Request.Query["site"].ToString();
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            string range = context.Request.Query["range"].ToString();
            if (range.Length == 0)
            {
                range = "consume";
            }

            int[] types = range switch
            {
                "all" => Array.Empty<int>(),
                "consume" => new[] { 2 },
                _ => new[] { 2 },
            };

            // [段1] 分页参数——缺省取默认值；给了但非法一律报错（入口面零容忍，不静默回落）
            int offset = 0;
            string offsetRaw = context.Request.Query["offset"].ToString();
            if (offsetRaw.Length > 0 && (!int.TryParse(offsetRaw, out offset) || offset < 0))
            {
                return Results.Json(new { ok = false, error = "offset 非法：" + offsetRaw });
            }

            int limit = 200;
            string limitRaw = context.Request.Query["limit"].ToString();
            if (limitRaw.Length > 0 && (!int.TryParse(limitRaw, out limit) || limit < 1 || limit > 500))
            {
                return Results.Json(new { ok = false, error = "limit 非法（1-500）：" + limitRaw });
            }

            using var db = new Db(dbPath);
            long total = db.RequestIdTotal(site.Id, types);
            List<RequestIdRow> items = db.RequestIds(site.Id, types, limit, offset);

            return Results.Json(new
            {
                ok = true,
                total = total,
                offset = offset,
                limit = limit,
                items = items.Select(r => new { requestId = r.RequestId, createdAt = r.CreatedAt }),
            });
        });

        // [段8] 启动浏览器并打地址
        string url = "http://127.0.0.1:" + config.Port + "/";
        Console.WriteLine("ApiSiteAnalyzer 面板已启动：" + url);
        Console.WriteLine("站点：" + string.Join(" / ", sites.Select(s => s.DisplayName)));
        Console.WriteLine("自动采集：" + (settings.AutoEnabled ? "开（每 " + settings.AutoIntervalSeconds + " 秒）" : "关"));

        _ = collector.RunAsync(ct);
        OpenBrowser(url);

        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>本进程程序集版本（版本自证用）。</summary>
    /// <returns>版本文本（如 0.3.0）。</returns>
    private static string AppVersion()
    {
        var assembly = typeof(ServeRunner).Assembly;
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            return info;
        }

        return assembly.GetName().Version?.ToString() ?? "未知";
    }

    /// <summary>
    /// 扫描程序目录下的槽位产物（ApiSiteAnalyzer_*.exe），取版本最高的一个。
    /// 双槽的意义：运行中的 exe 锁文件，新产物写另一槽即可覆盖部署（KKManager 判例）。
    /// </summary>
    /// <returns>槽位版本 + 文件名；无槽位产物返回 null。</returns>
    private static (string Version, string FileName)? FindNewestSlot()
    {
        string dir = AppContext.BaseDirectory;
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return null;
        }

        (string Version, string FileName)? best = null;
        foreach (string path in Directory.EnumerateFiles(dir, "ApiSiteAnalyzer_*.exe"))
        {
            string version = ReadFileVersion(path);
            if (version.Length == 0)
            {
                continue;
            }

            if (best is null || IsNewer(version, best.Value.Version))
            {
                best = (version, Path.GetFileName(path));
            }
        }

        return best;
    }

    /// <summary>读 PE 文件版本（读不到返回空串——按「非候选」处理，不静默当成最新）。</summary>
    private static string ReadFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>判断候选版本是否比当前版本新（解析失败一律按「不新」——不误报更新）。</summary>
    private static bool IsNewer(string candidate, string current)
    {
        Version? left = ParseVersion(candidate);
        Version? right = ParseVersion(current);
        if (left is null || right is null)
        {
            return false;
        }

        return left > right;
    }

    /// <summary>把版本文本解析成可比较对象（截掉 -preview / +build 后缀）。</summary>
    private static Version? ParseVersion(string text)
    {
        string trimmed = text.Trim();
        int cut = trimmed.IndexOfAny(new[] { '-', '+' });
        if (cut > 0)
        {
            trimmed = trimmed.Substring(0, cut);
        }

        return Version.TryParse(trimmed, out Version? parsed) ? parsed : null;
    }

    /// <summary>把聚合行映射成前端结构。</summary>
    private static object Map(AggregateRow row, double unit)
    {
        return new
        {
            name = row.Name,
            count = row.Count,
            quota = row.Quota,
            amount = row.Quota / unit,
            promptTokens = row.PromptTokens,
            completionTokens = row.CompletionTokens,
            cacheTokens = row.CacheTokens,
        };
    }

    /// <summary>拉取一个站点并逐页入库。</summary>
    /// <param name="session">站点会话。</param>
    /// <param name="site">站点适配器。</param>
    /// <param name="dbPath">库路径。</param>
    /// <param name="state">面板运行态。</param>
    /// <param name="incremental">是否增量（追平历史即停）。</param>
    public static async Task FetchSiteAsync(SiteSession session, IApiSite site, string dbPath, PanelState state, bool incremental)
    {
        using var db = new Db(dbPath);

        FetchSummary summary = await session.FetchAllAsync(
            site,
            100,
            new FetchOptions { Incremental = incremental },
            (page, result) => PageWriter.Write(db, site, result),
            text => state.Progress(text),
            CancellationToken.None).ConfigureAwait(false);

        if (!summary.Ok)
        {
            if (summary.NotLoggedIn)
            {
                state.Fail("未登录——请点「去登录」在浏览器里登录后重试");
            }
            else
            {
                state.Fail(summary.Error);
            }

            return;
        }

        long rows = db.Count(site.Id);
        db.MarkFetched(site.Id, rows);
        state.SetBalance(summary.Balance);

        if (summary.Snapshot is not null)
        {
            db.SaveSnapshot(site.Id, summary.Snapshot);
        }

        state.Done("完成：拉取 " + summary.Pages + " 页 / " + summary.Fetched + " 条 · 新增 " + summary.Added +
            " 条 · 库内 " + rows + " 行" + (summary.StoppedEarly ? " · 已追平历史（提前停止）" : ""));
    }

    /// <summary>请求体字段缓存键（HttpContext.Items）。</summary>
    private const string FieldCacheKey = "asa.form.fields";

    /// <summary>
    /// 从请求体里读一个字段（JSON 或 form）。
    /// 🔴 **请求体只能读一次**——读第二次得到空串。故本方法把整份字段缓存进 `HttpContext.Items`，
    /// 同一请求内多次取字段复用同一份（否则第二个字段起静默变空，调用方只看到「值没生效」）。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="field">字段名。</param>
    /// <returns>字段值（缺 = 空串）。</returns>
    private static async Task<string> ReadFieldAsync(HttpContext context, string field)
    {
        if (!context.Items.TryGetValue(FieldCacheKey, out object? cached) || cached is not Dictionary<string, string> map)
        {
            map = await ReadAllFieldsAsync(context).ConfigureAwait(false);
            context.Items[FieldCacheKey] = map;
        }

        return map.TryGetValue(field, out string? value) ? value : "";
    }

    /// <summary>读整份请求字段（form 或 JSON 体，缺则查 query）。</summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <returns>字段表。</returns>
    private static async Task<Dictionary<string, string>> ReadAllFieldsAsync(HttpContext context)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            foreach (string key in form.Keys)
            {
                map[key] = form[key].ToString();
            }

            return map;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (body.Trim().Length == 0)
        {
            foreach (var pair in context.Request.Query)
            {
                map[pair.Key] = pair.Value.ToString();
            }

            return map;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in doc.RootElement.EnumerateObject())
                {
                    map[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Number => property.Value.ToString(),
                        _ => "",
                    };
                }
            }
        }
        catch (JsonException)
        {
            // 体不是合法 JSON——按空字段表处理（调用方按缺值处置）
        }

        return map;
    }

    /// <summary>按站点键找适配器。</summary>
    private static IApiSite? FindSite(List<IApiSite> sites, string siteId)
    {
        foreach (IApiSite site in sites)
        {
            if (string.Equals(site.Id, siteId, StringComparison.OrdinalIgnoreCase))
            {
                return site;
            }
        }

        return null;
    }

    /// <summary>读内嵌单页。</summary>
    private static string ReadIndex()
    {
        var assembly = typeof(ServeRunner).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(IndexResource);
        if (stream is null)
        {
            return "<!doctype html><meta charset=\"utf-8\"><h1>面板资源缺失</h1><p>内嵌单页 " + IndexResource + " 未找到</p>";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>用系统默认浏览器打开面板。</summary>
    private static void OpenBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception)
        {
            // 打不开浏览器不算失败——控制台已打印地址
        }
    }
}
