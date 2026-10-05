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
        var watch = new WorkWatch(session, sites, dbPath, state, collector);

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
                //        通道判定收敛在 SiteAggregate（总览站点块用同一份实现，不双写）
                string apiKey = SiteAggregate.ApiKeyOf(site);
                list.Add(new
                {
                    id = site.Id,
                    displayName = site.DisplayName,
                    baseUrl = site.BaseUrl,
                    channel = SiteAggregate.ChannelOf(site),
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
                    cardOrder = settings.CardOrder,
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

        // [段2c] 保存卡片顺序（面板拖拽排定）——落 data/settings.json
        //        🔴 独立端点，不走 /api/settings：那个端点带 collector.Reset()（重置自动采集计时），
        //        拖一次卡片就打乱一次采集节奏是不可接受的副作用
        app.MapPost("/api/card-order", async (HttpContext context) =>
        {
            string raw = await ReadFieldAsync(context, "order");

            // 入口面零容忍——未知键报错拒绝，不静默剔除
            if (!settings.SetCardOrder(raw, out string error))
            {
                return Results.Json(new { ok = false, error = error });
            }

            return Results.Json(new { ok = true, cardOrder = settings.CardOrder });
        });

        // [段3] 登录态探测——未登录时面板提示「去登录」
        app.MapPost("/api/login-check", async (HttpContext context) =>
        {
            string siteId = await ReadFieldAsync(context, "site");

            // [段3a] 总览——对**全部站点**依次探测（总览不是站点，动作落在每个站点上）；
            //        单站失败不中断整轮，逐站如实回报（结果同时写进各站登录态缓存）
            if (IsAllSites(siteId))
            {
                var probed = new List<object>();
                int loggedInCount = 0;
                foreach (IApiSite item in sites)
                {
                    try
                    {
                        LoginState probe = await queue.RunAsync(() => session.ProbeLoginAsync(item, CancellationToken.None)).ConfigureAwait(false);
                        state.SetBalance(item.Id, probe.Quota);
                        state.SetLogin(item.Id, probe.LoggedIn, probe.Username, probe.LoggedIn ? "" : probe.Message);
                        if (probe.LoggedIn)
                        {
                            loggedInCount = loggedInCount + 1;
                        }

                        probed.Add(new
                        {
                            id = item.Id,
                            displayName = item.DisplayName,
                            channel = SiteAggregate.ChannelOf(item),
                            loggedIn = probe.LoggedIn,
                            username = probe.Username,
                            quota = probe.Quota,
                            requestCount = probe.RequestCount,
                            message = probe.Message,
                        });
                    }
                    catch (Exception ex)
                    {
                        probed.Add(new
                        {
                            id = item.Id,
                            displayName = item.DisplayName,
                            channel = SiteAggregate.ChannelOf(item),
                            loggedIn = false,
                            username = "",
                            quota = 0L,
                            requestCount = 0L,
                            message = "检查失败：" + ex.Message,
                        });
                    }
                }

                return Results.Json(new
                {
                    ok = true,
                    all = true,
                    total = sites.Count,
                    loggedInCount = loggedInCount,
                    sites = probed,
                });
            }

            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            try
            {
                LoginState login = await queue.RunAsync(() => session.ProbeLoginAsync(site, CancellationToken.None)).ConfigureAwait(false);
                state.SetBalance(site.Id, login.Quota);
                state.SetLogin(site.Id, login.LoggedIn, login.Username, login.LoggedIn ? "" : login.Message);
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

            // [段4a] 总览——对**全部站点**依次打开登录页（总览不是站点，动作落在每个站点上）；
            //        直连通道没有浏览器登录这回事、已确认已登录的站点跳过（重复导航会清掉页面上的 token 缓存）
            if (IsAllSites(siteId))
            {
                var results = new List<object>();
                int openedCount = 0;
                foreach (IApiSite item in sites)
                {
                    if (SiteAggregate.ChannelOf(item) == "apikey")
                    {
                        results.Add(new
                        {
                            id = item.Id,
                            displayName = item.DisplayName,
                            opened = false,
                            message = "走 API key 直连通道，不需要浏览器登录",
                        });
                        continue;
                    }

                    LoginSnapshot? known = state.LoginOf(item.Id);
                    if (known is not null && known.LoggedIn)
                    {
                        results.Add(new
                        {
                            id = item.Id,
                            displayName = item.DisplayName,
                            opened = false,
                            message = "已登录" + (known.Username.Length > 0 ? "（" + known.Username + "）" : "") + "——跳过",
                        });
                        continue;
                    }

                    try
                    {
                        BrowserInstance instance = await queue.RunAsync(() => session.OpenLoginAsync(item, CancellationToken.None)).ConfigureAwait(false);
                        openedCount = openedCount + 1;
                        results.Add(new
                        {
                            id = item.Id,
                            displayName = item.DisplayName,
                            opened = true,
                            message = "已打开登录页（端口 " + instance.Port + "）",
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new
                        {
                            id = item.Id,
                            displayName = item.DisplayName,
                            opened = false,
                            message = "打开失败：" + ex.Message,
                        });
                    }
                }

                return Results.Json(new
                {
                    ok = true,
                    all = true,
                    total = sites.Count,
                    opened = openedCount,
                    sites = results,
                });
            }

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
                state.SetLogin(siteId, probe.LoggedIn, probe.Username, probe.LoggedIn ? "" : probe.Message);
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

            // [段5a] 总览——对**全部站点**依次采集（总览不是站点，采集永远发生在具体站点上）；
            //        整轮占一个串行槽位，批量期间单站的完成 / 失败不结束这一轮（收口归 EndBatch）
            if (IsAllSites(siteId))
            {
                bool batchIncremental = full != "true" && settings.Incremental;
                if (!state.TryBeginBatch("全部站点", sites.Count))
                {
                    return Results.Json(new { ok = false, error = "已有采集在进行中" });
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FetchAllSitesAsync(session, sites, dbPath, state, batchIncremental, collector).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        state.EndBatch("全站采集失败：" + ex.Message);
                    }
                });

                return Results.Json(new
                {
                    ok = true,
                    incremental = batchIncremental,
                    message = "已开始全站采集（" + sites.Count + " 站，依次执行）",
                });
            }

            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            bool incremental = full != "true" && settings.Incremental;
            if (!state.TryBegin(site.Id, site.DisplayName))
            {
                return Results.Json(new { ok = false, error = "已有采集在进行中" });
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchSiteAsync(session, site, dbPath, state, incremental, collector).ConfigureAwait(false);
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

        // [段7] 分析面——site=__all 为总览（跨站合并），否则为单站
        app.MapGet("/api/analysis", (HttpContext context) =>
        {
            string siteId = context.Request.Query["site"].ToString();
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

            // [段0] 按小时图的日期筛选——给了就必须是 yyyy-MM-dd（入口面零容忍，不静默回落成「当前」视图）
            string day = context.Request.Query["day"].ToString();
            if (day.Length > 0 && !DateTime.TryParseExact(day, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime _))
            {
                return Results.Json(new { ok = false, error = "day 非法（yyyy-MM-dd）：" + day });
            }

            // [段7a] 总览——合并全部站点的记录；不是真实站点，故在站点查找之前分流
            if (IsAllSites(siteId))
            {
                return Results.Json(BuildAllSitesAnalysis(dbPath, sites, types, state, day));
            }

            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            using var db = new Db(dbPath);
            OverviewStat overview = db.Overview(site.Id, types);
            List<AggregateRow> byModel = db.ByModel(site.Id, types, 30);
            List<AggregateRow> byDay = db.ByDay(site.Id, types);
            /* 「当前」口径（滚动 24 小时）恒算一次——按天列表顶部的「当前」行在选中某天时也要对，故不随 day 走 */
            List<AggregateRow> byHourCurrent = db.ByHour(site.Id, types, "");
            List<AggregateRow> byHour = day.Length > 0 ? db.ByHour(site.Id, types, day) : byHourCurrent;
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
                    prevSampleCount = overview.PrevSampleCount,
                    prevAvgFirstTokenMs = overview.PrevAvgFirstTokenMs,
                    prevAvgUseTime = overview.PrevAvgUseTime,
                    prevAvgSpeedTps = overview.PrevAvgSpeedTps,
                    last24h = MapWindow(overview.Last24h, overview.Last24h.Quota / unit),
                    prev24h = MapWindow(overview.Prev24h, overview.Prev24h.Quota / unit),
                },
                byModel = byModel.Select(r => Map(r, unit)),
                byDay = byDay.Select(r => Map(r, unit)),
                byHour = byHour.Select(r => Map(r, unit)),
                current = Map(Db.SumRows(byHourCurrent, "当前"), unit),
                byToken = byToken.Select(r => Map(r, unit)),
                byGroup = byGroup.Select(r => Map(r, unit)),
                recent = recent.Select(r => MapRecent(r, site.DisplayName, unit)),
            });
        });

        // [段7b] 已探明请求 ID 明细——卡片点开的逐条清单（去重后按时刻倒序分页）；site=__all 为跨站合并
        app.MapGet("/api/request-ids", (HttpContext context) =>
        {
            string siteId = context.Request.Query["site"].ToString();

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

            int limit = 50;
            string limitRaw = context.Request.Query["limit"].ToString();
            if (limitRaw.Length > 0 && (!int.TryParse(limitRaw, out limit) || limit < 1 || limit > 500))
            {
                return Results.Json(new { ok = false, error = "limit 非法（1-500）：" + limitRaw });
            }

            // [段2] 总览——各站先各取前 offset+limit 条，合并排序后切片
            //        （跨站第 N 条必在各站前 N 条之内，故不漏；各站请求 ID 独立，total 按站相加）
            if (IsAllSites(siteId))
            {
                using var allDb = new Db(dbPath);
                var merged = new List<UsageRecord>();
                var units = new Dictionary<string, double>(StringComparer.Ordinal);
                long allTotal = 0;
                foreach (IApiSite item in sites)
                {
                    allTotal += allDb.RequestIdTotal(item.Id, types);
                    merged.AddRange(allDb.RequestIds(item.Id, types, offset + limit, 0));
                    units[item.Id] = item.QuotaPerUnit <= 0 ? 500000 : item.QuotaPerUnit;
                }

                merged.Sort(CompareRequestId);
                return Results.Json(new
                {
                    ok = true,
                    total = allTotal,
                    offset = offset,
                    limit = limit,
                    items = merged.Skip(offset).Take(limit)
                        .Select(r => MapRecent(r, DisplayNameOf(sites, r.SiteId), units.TryGetValue(r.SiteId, out double unit) ? unit : 500000)),
                });
            }

            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            using var db = new Db(dbPath);
            long total = db.RequestIdTotal(site.Id, types);
            List<UsageRecord> items = db.RequestIds(site.Id, types, limit, offset);
            double siteUnit = site.QuotaPerUnit <= 0 ? 500000 : site.QuotaPerUnit;

            return Results.Json(new
            {
                ok = true,
                total = total,
                offset = offset,
                limit = limit,
                items = items.Select(r => MapRecent(r, site.DisplayName, siteUnit)),
            });
        });

        // [段7c] 线程分析——取最近 200 条串链并标并发（**只分析不落库**——标记是运行时结论，不进 SQLite）
        app.MapGet("/api/thread-analysis", (HttpContext context) =>
        {
            string siteId = context.Request.Query["site"].ToString();

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

            if (!IsAllSites(siteId))
            {
                IApiSite? only = FindSite(sites, siteId);
                if (only is null)
                {
                    return Results.Json(new { ok = false, error = "未知站点：" + siteId });
                }

                ThreadAnalysisResult one = BuildThreadAnalysis(dbPath, new List<IApiSite> { only }, types);
                return Results.Json(MapThreadAnalysis(one));
            }

            ThreadAnalysisResult merged = BuildThreadAnalysis(dbPath, sites, types);
            return Results.Json(MapThreadAnalysis(merged));
        });

        // [段7d] 过去 24 小时的小时桶——「本地消费金额」卡片下方小字点开的子窗口（逐小时展示消耗金额）
        //         口径 = 滚动 24 小时（与卡片小字、「当前」行、按小时图同一份取数）——三处数字必然相等
        app.MapGet("/api/amount-hours", (HttpContext context) =>
        {
            string siteId = context.Request.Query["site"].ToString();

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

            // [段1] 总览——各站桶按「日期 + 小时」对齐后合并（金额按各站换算比折算后相加）
            if (IsAllSites(siteId))
            {
                using var allDb = new Db(dbPath);
                List<HourBucket> merged = SiteAggregate.MergeBuckets(allDb, sites, types);
                return Results.Json(new
                {
                    ok = true,
                    site = new
                    {
                        id = AllSitesKey,
                        displayName = "总览（全部站点）",
                        currencySymbol = SiteAggregate.SymbolOf(sites),
                        quotaPerUnit = 0,
                    },
                    hours = merged.Select(b => MapBucket(b, 0, true)),
                    total = MapBucketTotal(merged, 0, true),
                });
            }

            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            using var db = new Db(dbPath);
            List<HourBucket> buckets = db.RollingBuckets(site.Id, types);
            double unit = site.QuotaPerUnit <= 0 ? 500000 : site.QuotaPerUnit;
            return Results.Json(new
            {
                ok = true,
                site = new { id = site.Id, displayName = site.DisplayName, currencySymbol = site.CurrencySymbol, quotaPerUnit = unit },
                hours = buckets.Select(b => MapBucket(b, unit, false)),
                total = MapBucketTotal(buckets, unit, false),
            });
        });

        // [段8] 启动浏览器并打地址
        string url = "http://127.0.0.1:" + config.Port + "/";
        Console.WriteLine("ApiSiteAnalyzer 面板已启动：" + url);
        Console.WriteLine("站点：" + string.Join(" / ", sites.Select(s => s.DisplayName)));
        Console.WriteLine("自动采集：" + (settings.AutoEnabled ? "开（每 " + settings.AutoIntervalSeconds + " 秒）" : "关"));

        _ = collector.RunAsync(ct);
        _ = watch.RunAsync(ct);
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

    /// <summary>总览键——面板下拉第一项，代表「合并全部站点」；不是真实站点（无适配器、不可采集）。</summary>
    public const string AllSitesKey = "__all";

    /// <summary>是否总览键。</summary>
    /// <param name="siteId">站点键（或总览键）。</param>
    /// <returns>是总览返回 true。</returns>
    private static bool IsAllSites(string siteId)
    {
        return string.Equals(siteId, AllSitesKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按站点键取显示名（找不到回落键本身——不静默成空串）。</summary>
    /// <param name="sites">站点清单。</param>
    /// <param name="siteId">站点键。</param>
    /// <returns>显示名。</returns>
    private static string DisplayNameOf(List<IApiSite> sites, string siteId)
    {
        IApiSite? site = FindSite(sites, siteId);
        return site is null ? siteId : site.DisplayName;
    }

    /// <summary>请求 ID 明细排序——时刻倒序、请求 ID 升序（与库内分页排序同序，跨站合并后仍稳定）。</summary>
    /// <param name="left">左。</param>
    /// <param name="right">右。</param>
    /// <returns>比较结果。</returns>
    private static int CompareRequestId(UsageRecord left, UsageRecord right)
    {
        int byTime = right.CreatedAt.CompareTo(left.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.RequestId, right.RequestId);
    }

    /// <summary>
    /// 组装总览（跨站合并）响应——合并口径与取样边界见 SiteAggregate。
    /// 金额一律按各站换算比折算（各站 quota 单位不同，直接相加无意义）。
    /// </summary>
    /// <param name="dbPath">库路径。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="types">记录类型白名单（空 = 全部）。</param>
    /// <param name="state">面板运行态（取各站最近一次探到的真实余额）。</param>
    /// <param name="day">限定「按小时」的日期（yyyy-MM-dd；空 = 「当前」滚动口径）。</param>
    /// <returns>响应对象。</returns>
    private static object BuildAllSitesAnalysis(string dbPath, List<IApiSite> sites, int[] types, PanelState state, string day)
    {
        using var db = new Db(dbPath);
        AllSitesResult all = SiteAggregate.Build(db, sites, types, state.BalanceOf, day);
        var blocks = new Dictionary<string, SiteBlock>(StringComparer.Ordinal);
        foreach (SiteBlock block in all.Sites)
        {
            blocks[block.Id] = block;
        }

        return new
        {
            ok = true,
            site = new
            {
                id = AllSitesKey,
                displayName = "总览（全部站点）",
                currencySymbol = all.CurrencySymbol,
                quotaPerUnit = 0,
            },
            overview = new
            {
                count = all.Count,
                quota = 0L,
                amount = all.Amount,
                promptTokens = all.PromptTokens,
                completionTokens = all.CompletionTokens,
                cacheTokens = all.CacheTokens,
                cacheHitRate = all.PromptTokens > 0 ? (double)all.CacheTokens / all.PromptTokens : 0,
                requestIdCount = all.RequestIdCount,
                recentAvgFirstTokenMs = all.RecentAvgFirstTokenMs,
                recentAvgUseTime = all.RecentAvgUseTime,
                recentAvgSpeedTps = all.RecentAvgSpeedTps,
                recentSampleCount = all.RecentSampleCount,
                prevSampleCount = all.PrevSampleCount,
                prevAvgFirstTokenMs = all.PrevAvgFirstTokenMs,
                prevAvgUseTime = all.PrevAvgUseTime,
                prevAvgSpeedTps = all.PrevAvgSpeedTps,
                last24h = MapWindow(all.Last24h, all.Last24hAmount),
                prev24h = MapWindow(all.Prev24h, all.Prev24hAmount),
            },
            byModel = all.ByModel.Select(MapMerged),
            byDay = all.ByDay.Select(MapMerged),
            byHour = all.ByHour.Select(MapMerged),
            current = MapMerged(all.CurrentRow),
            byToken = all.ByToken.Select(MapMerged),
            byGroup = all.ByGroup.Select(MapMerged),
            recent = all.Recent.Select(r => MapRecent(r, DisplayNameOf(sites, r.SiteId), UnitOf(blocks, r.SiteId))),
            sites = all.Sites.Select(MapSiteBlock),
        };
    }

    /// <summary>取站点块的换算比（找不到回落 New API 默认 500000——不静默按 0 除）。</summary>
    /// <param name="blocks">站点块表。</param>
    /// <param name="siteId">站点键。</param>
    /// <returns>每货币单位 quota 数。</returns>
    private static double UnitOf(Dictionary<string, SiteBlock> blocks, string siteId)
    {
        return blocks.TryGetValue(siteId, out SiteBlock? block) && block.QuotaPerUnit > 0 ? block.QuotaPerUnit : 500000;
    }

    /// <summary>把站点块映射成前端结构（总览视图的站点块区）。</summary>
    /// <param name="block">站点块。</param>
    /// <returns>前端结构。</returns>
    private static object MapSiteBlock(SiteBlock block)
    {
        return new
        {
            id = block.Id,
            displayName = block.DisplayName,
            channel = block.Channel,
            rows = block.Rows,
            count = block.Count,
            amount = block.Amount,
            promptTokens = block.PromptTokens,
            completionTokens = block.CompletionTokens,
            cacheTokens = block.CacheTokens,
            cacheHitRate = block.CacheHitRate,
            balance = block.Balance,
            unlimitedBalance = block.UnlimitedBalance,
            quotaPerUnit = block.QuotaPerUnit,
            currencySymbol = block.CurrencySymbol,
            lastFetchAt = block.LastFetchAt,
        };
    }

    /// <summary>把时间窗口统计映射成前端结构（命中率由输入 / 缓存现算，金额由调用方按站点换算比折算后传入——口径只在服务端定一次）。</summary>
    /// <param name="window">窗口统计。</param>
    /// <returns>前端结构。</returns>
    /// <param name="amount">窗口内折算金额（单站 = quota / 该站换算比；总览 = 各站折算后相加——调用方给，映射器不猜单位）。</param>
    private static object MapWindow(WindowStat window, double amount)
    {
        return new
        {
            count = window.Count,
            quota = window.Quota,
            amount = amount,
            promptTokens = window.PromptTokens,
            completionTokens = window.CompletionTokens,
            cacheTokens = window.CacheTokens,
            cacheHitRate = window.PromptTokens > 0 ? (double)window.CacheTokens / window.PromptTokens : 0,
            requestIdCount = window.RequestIdCount,
        };
    }

    /// <summary>把小时桶映射成前端结构（子窗口逐行用——金额口径与调用方一致：单站现算 / 总览取合并值）。</summary>
    /// <param name="bucket">小时桶。</param>
    /// <param name="unit">每货币单位 quota 数（总览传 0——金额取桶内已折算值）。</param>
    /// <param name="merged">是否跨站合并行（true = 金额取桶内已折算值）。</param>
    /// <returns>前端结构。</returns>
    private static object MapBucket(HourBucket bucket, double unit, bool merged)
    {
        return new
        {
            day = bucket.Day,
            hour = bucket.Hour,
            count = bucket.Count,
            quota = bucket.Quota,
            amount = merged ? bucket.Amount : bucket.Quota / unit,
            promptTokens = bucket.PromptTokens,
            completionTokens = bucket.CompletionTokens,
            cacheTokens = bucket.CacheTokens,
            requestIdCount = bucket.RequestIdCount,
        };
    }

    /// <summary>把小时桶列表合计成一行（子窗口标题的「合计」用它——与逐行数字同源，前端不自行累加）。</summary>
    /// <param name="buckets">小时桶。</param>
    /// <param name="unit">每货币单位 quota 数（总览传 0——金额取合并值）。</param>
    /// <param name="merged">是否跨站合并（true = 金额取桶内已折算值）。</param>
    /// <returns>合计行。</returns>
    private static object MapBucketTotal(List<HourBucket> buckets, double unit, bool merged)
    {
        long count = 0;
        long quota = 0;
        long requestIds = 0;
        double amount = 0;
        foreach (HourBucket bucket in buckets)
        {
            count += bucket.Count;
            quota += bucket.Quota;
            requestIds += bucket.RequestIdCount;
            amount += merged ? bucket.Amount : bucket.Quota / unit;
        }

        return new { count = count, quota = quota, amount = amount, requestIdCount = requestIds };
    }

    /// <summary>把聚合行映射成前端结构（单站口径——额度按本站换算比折算）。</summary>
    /// <param name="row">聚合行。</param>
    /// <param name="unit">每货币单位 quota 数。</param>
    /// <returns>前端结构。</returns>
    private static object Map(AggregateRow row, double unit)
    {
        return MapRow(row.Name, row.Count, row.Quota, row.Quota / unit, row.PromptTokens, row.CompletionTokens, row.CacheTokens, row.Token, row.RequestIdCount);
    }

    /// <summary>把跨站合并行映射成前端结构（金额已在合并时按各站换算比折算）。</summary>
    /// <param name="row">合并行。</param>
    /// <returns>前端结构。</returns>
    private static object MapMerged(MergedRow row)
    {
        return MapRow(row.Name, row.Count, row.Quota, row.Amount, row.PromptTokens, row.CompletionTokens, row.CacheTokens, row.Token, row.RequestIdCount);
    }

    /// <summary>聚合行的前端结构（单站与总览共用同一份形状——两处各写一份必漂移）。</summary>
    /// <param name="name">分组名。</param>
    /// <param name="count">条数（= 请求数）。</param>
    /// <param name="quota">原始额度。</param>
    /// <param name="amount">折算金额。</param>
    /// <param name="promptTokens">输入 token。</param>
    /// <param name="completionTokens">输出 token。</param>
    /// <param name="cacheTokens">缓存 token。</param>
    /// <param name="token">三种 token 之和（输入 + 输出 + 缓存）——图表的排序与显示口径。</param>
    /// <param name="requestIdCount">已探明请求 ID 数（去重）。</param>
    /// <returns>前端结构。</returns>
    private static object MapRow(string name, long count, long quota, double amount, long promptTokens, long completionTokens, long cacheTokens, long token, long requestIdCount)
    {
        return new
        {
            name = name,
            count = count,
            quota = quota,
            amount = amount,
            promptTokens = promptTokens,
            completionTokens = completionTokens,
            cacheTokens = cacheTokens,
            token = token,
            requestIdCount = requestIdCount,
        };
    }

    /// <summary>把一条库行映射成前端结构（金额按该行所属站点折算——总览里每行单位可能不同）。</summary>
    /// <param name="record">库行。</param>
    /// <param name="siteName">所属站点显示名。</param>
    /// <param name="unit">所属站点每货币单位 quota 数。</param>
    /// <returns>前端结构。</returns>
    private static object MapRecent(UsageRecord record, string siteName, double unit)
    {
        return new
        {
            id = record.RemoteId,
            siteId = record.SiteId,
            siteName = siteName,
            createdAt = record.CreatedAt,
            type = record.Type,
            modelName = record.ModelName,
            tokenName = record.TokenName,
            groupName = record.GroupName,
            quota = record.Quota,
            amount = record.Quota / unit,
            promptTokens = record.PromptTokens,
            completionTokens = record.CompletionTokens,
            cacheTokens = record.CacheTokens,
            useTime = record.UseTime,
            firstTokenMs = record.FirstTokenMs,
            speedTps = record.SpeedTps,
            isStream = record.IsStream,
            requestId = record.RequestId,
            upstreamModel = record.UpstreamModel,
            content = record.Content,
        };
    }
    /// <summary>
    /// 组装线程分析结果——取最近 N 条记录（按时刻升序）串链并标并发。
    /// 总览（跨站合并）先各站取最近 N 条再合并取前 N 条——跨站前 N 条必在各站前 N 条之内，故不漏。
    /// </summary>
    /// <param name="dbPath">库文件路径。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <returns>线程分析结果（链清单 + 并发统计）。</returns>
    private static ThreadAnalysisResult BuildThreadAnalysis(string dbPath, List<IApiSite> sites, int[] types)
    {
        var sample = new List<UsageRecord>();

        using (var db = new Db(dbPath))
        {
            if (sites.Count == 1 && !IsAllSites(sites[0].Id))
            {
                sample.AddRange(db.Recent(sites[0].Id, types, ThreadAnalyzer.SampleLimit));
            }
            else
            {
                foreach (IApiSite site in sites)
                {
                    sample.AddRange(db.Recent(site.Id, types, ThreadAnalyzer.SampleLimit));
                }
            }
        }

        // [段1] 升序化——串链按时间推进，样本必须是时刻升序（Db.Recent 给的是倒序）
        sample.Sort((left, right) =>
        {
            int byTime = left.CreatedAt.CompareTo(right.CreatedAt);
            if (byTime != 0)
            {
                return byTime;
            }

            int byRemote = left.RemoteId.CompareTo(right.RemoteId);
            if (byRemote != 0)
            {
                return byRemote;
            }

            return string.CompareOrdinal(left.SiteId, right.SiteId);
        });

        if (sample.Count > ThreadAnalyzer.SampleLimit)
        {
            sample = sample.Skip(sample.Count - ThreadAnalyzer.SampleLimit).ToList();
        }

        return ThreadAnalyzer.Analyze(sample, siteId => DisplayNameOf(sites, siteId));
    }
    /// <summary>把线程分析结果映射成前端结构（链 / 轮 / 并发标记同源一份形状）。</summary>
    /// <param name="result">分析结果。</param>
    /// <returns>前端结构。</returns>
    private static object MapThreadAnalysis(ThreadAnalysisResult result)
    {
        return new
        {
            ok = true,
            sampleCount = result.SampleCount,
            sampleLimit = result.SampleLimit,
            chainCount = result.ChainCount,
            concurrentStepCount = result.ConcurrentStepCount,
            concurrentChainCount = result.ConcurrentChainCount,
            forkCandidateCount = result.ForkCandidateCount,
            chains = result.Chains.Select(chain => new
            {
                index = chain.Index,
                hasConcurrent = chain.HasConcurrent,
                hasForkCandidate = chain.HasForkCandidate,
                steps = chain.Steps.Select(step => new
                {
                    index = step.Index,
                    createdAt = step.CreatedAt,
                    promptTokens = step.PromptTokens,
                    completionTokens = step.CompletionTokens,
                    expectNext = step.ExpectNext,
                    requestId = step.RequestId,
                    modelName = step.ModelName,
                    siteName = step.SiteName,
                    concurrent = step.Concurrent,
                    concurrentReason = step.ConcurrentReason,
                    forkCandidate = step.ForkCandidate,
                    forkCandidateReason = step.ForkCandidateReason,
                    forkTargets = step.ForkTargets,
                }),
            }),
        };
    }

    /// <summary>拉取一个站点并逐页入库。</summary>
    /// <param name="session">站点会话。</param>
    /// <param name="site">站点适配器。</param>
    /// <param name="dbPath">库路径。</param>
    /// <param name="state">面板运行态。</param>
    /// <param name="incremental">是否增量（追平历史即停）。</param>
    /// <param name="collector">自动采集器（结果上报口）——手动 / 全站 / 反查三条路径传它；自动轮次传 null（它在 RunOnceAsync 里自行落状态，避免同一轮被记两次）。</param>
    /// <returns>拉取汇总（含新增条数——自动采集据此决定是否降级间隔）。</returns>
    public static async Task<FetchSummary> FetchSiteAsync(SiteSession session, IApiSite site, string dbPath, PanelState state, bool incremental, AutoCollector? collector)
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
                // 掉登录在总览站点块上直接可见——不靠「采集失败」这类间接信号去推断
                state.SetLogin(site.Id, false, "", "未登录——请点「去登录」在浏览器里登录后重试");
                state.Fail("未登录——请点「去登录」在浏览器里登录后重试");
            }
            else
            {
                state.Fail(summary.Error);
            }

            // [段0] 结果上报——失败同样入账：连续失败达上限即停该站（自动 / 手动 / 全站 / 反查口径一致）
            if (collector is not null)
            {
                collector.ReportFetchResult(site.Id, false, 0, summary.NotLoggedIn ? "未登录" : summary.Error, false);
            }

            return summary;
        }

        // [段1] 登录态——直连通道站点拿不到账号名，沿用上次探活记下的那个（不因一次采集把名字擦掉）
        string username = summary.Username;
        if (username.Length == 0)
        {
            LoginSnapshot? previous = state.LoginOf(site.Id);
            username = previous is null ? "" : previous.Username;
        }

        state.SetLogin(site.Id, true, username, "");

        long rows = db.Count(site.Id);
        db.MarkFetched(site.Id, rows);
        state.SetBalance(site.Id, summary.Balance);

        if (summary.Snapshot is not null)
        {
            db.SaveSnapshot(site.Id, summary.Snapshot);
        }

        state.Done("完成：拉取 " + summary.Pages + " 页 / " + summary.Fetched + " 条 · 新增 " + summary.Added +
            " 条 · 库内 " + rows + " 行" + (summary.StoppedEarly ? " · 已追平历史（提前停止）" : ""));

        // [段2] 结果上报——成功即解除停采并清失败计数（能取到数 = 站点已恢复）；有新增即回基准间隔
        if (collector is not null)
        {
            collector.ReportFetchResult(site.Id, true, summary.Added, "", false);
        }

        return summary;
    }

    /// <summary>
    /// 全站采集——总览视图的「拉取数据」对全部站点**依次**执行（总览不是站点，采集永远落在具体站点上）。
    /// 采集动作串行（同一浏览器会话不能被并发驱动）；某站失败只记该站原因，不中断整轮；
    /// 整轮由 EndBatch 收口——批量期间单站的完成 / 失败不结束这一轮（面板进度一直显示「第几站 / 共几站」）。
    /// </summary>
    /// <param name="session">站点会话。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="dbPath">库路径。</param>
    /// <param name="state">面板运行态（整轮的进度与收口都在这里）。</param>
    /// <param name="incremental">是否增量（追平历史即停）。</param>
    /// <param name="collector">自动采集器（结果上报口——逐站结果都落到各站状态上，成功即解除该站停采）。</param>
    /// <returns>异步任务。</returns>
    public static async Task FetchAllSitesAsync(SiteSession session, List<IApiSite> sites, string dbPath, PanelState state, bool incremental, AutoCollector? collector)
    {
        var failures = new List<string>();
        int okCount = 0;
        int addedTotal = 0;

        foreach (IApiSite site in sites)
        {
            state.NextBatchSite(site.Id, site.DisplayName);
            try
            {
                FetchSummary summary = await FetchSiteAsync(session, site, dbPath, state, incremental, collector).ConfigureAwait(false);
                if (summary.Ok)
                {
                    okCount = okCount + 1;
                    addedTotal = addedTotal + summary.Added;
                }
                else
                {
                    failures.Add(site.DisplayName + "：" + (summary.NotLoggedIn ? "未登录" : summary.Error));
                }
            }
            catch (Exception ex)
            {
                state.Fail("采集失败：" + ex.Message);
                failures.Add(site.DisplayName + "：" + ex.Message);
            }
        }

        string text = "全站采集完成：" + sites.Count + " 站 · 成功 " + okCount + " · 新增 " + addedTotal + " 条";
        if (failures.Count > 0)
        {
            text = text + " · 失败 " + failures.Count + " 站（" + string.Join("；", failures) + "）";
        }

        state.EndBatch(text);
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
