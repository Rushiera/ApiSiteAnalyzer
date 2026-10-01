using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                list.Add(new
                {
                    id = site.Id,
                    displayName = site.DisplayName,
                    baseUrl = site.BaseUrl,
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

            return Results.Json(new { ok = true, sites = list, running = state.Running });
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

        // [段5] 拉取——入队，后台跑；面板轮询 /api/progress
        app.MapPost("/api/fetch", async (HttpContext context) =>
        {
            string siteId = await ReadFieldAsync(context, "site");
            IApiSite? site = FindSite(sites, siteId);
            if (site is null)
            {
                return Results.Json(new { ok = false, error = "未知站点：" + siteId });
            }

            if (state.Running)
            {
                return Results.Json(new { ok = false, error = "已有采集在进行中" });
            }

            state.Begin(site.DisplayName);
            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchSiteAsync(session, site, dbPath, state).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    state.Fail(ex.Message);
                }
            });

            return Results.Json(new { ok = true, message = "已开始采集" });
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
                    cacheHitRate = overview.PromptTokens > 0 ? (double)overview.CacheTokens / (overview.PromptTokens + overview.CacheTokens) : 0,
                    avgFirstTokenMs = overview.AvgFirstTokenMs,
                    avgUseTime = overview.AvgUseTime,
                    avgSpeedTps = overview.AvgSpeedTps,
                    minCreatedAt = overview.MinCreatedAt,
                    maxCreatedAt = overview.MaxCreatedAt,
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
                    upstreamModel = r.UpstreamModel,
                    content = r.Content,
                }),
            });
        });

        // [段8] 启动浏览器并打地址
        string url = "http://127.0.0.1:" + config.Port + "/";
        Console.WriteLine("ApiSiteAnalyzer 面板已启动：" + url);
        Console.WriteLine("站点：" + string.Join(" / ", sites.Select(s => s.DisplayName)));
        OpenBrowser(url);

        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
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
    private static async Task FetchSiteAsync(SiteSession session, IApiSite site, string dbPath, PanelState state)
    {
        using var db = new Db(dbPath);

        FetchSummary summary = await session.FetchAllAsync(
            site,
            100,
            (page, result) =>
            {
                var buffer = new List<UsageRecord>();
                foreach (string record in result.Records)
                {
                    UsageRow row = site.ParseRow(record);
                    buffer.Add(new UsageRecord
                    {
                        SiteId = site.Id,
                        LogKey = row.LogKey,
                        RemoteId = row.RemoteId,
                        CreatedAt = row.CreatedAt,
                        Type = row.Type,
                        ModelName = row.ModelName,
                        TokenName = row.TokenName,
                        GroupName = row.Group,
                        Quota = row.Quota,
                        PromptTokens = row.PromptTokens,
                        CompletionTokens = row.CompletionTokens,
                        CacheTokens = row.CacheTokens,
                        UseTime = row.UseTime,
                        FirstTokenMs = row.FirstTokenMs,
                        SpeedTps = row.SpeedTps,
                        IsStream = row.IsStream,
                        Channel = row.Channel,
                        UpstreamModel = row.UpstreamModel,
                        RequestId = row.RequestId,
                        Content = row.Content,
                    });
                }

                db.Upsert(buffer);
                return true;
            },
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

        state.Done("完成：拉取 " + summary.Pages + " 页 / " + summary.Fetched + " 条 · 库内 " + rows + " 行");
    }

    /// <summary>从请求体里读一个表单字段（JSON 或 form）。</summary>
    private static async Task<string> ReadFieldAsync(HttpContext context, string field)
    {
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            return form[field].ToString();
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (body.Trim().Length == 0)
        {
            return context.Request.Query[field].ToString();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(field, out JsonElement value) ? (value.GetString() ?? "") : "";
        }
        catch (JsonException)
        {
            return "";
        }
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
