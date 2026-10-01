using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ApiSiteAnalyzer.Browser;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;

namespace ApiSiteAnalyzer.Collect;

/// <summary>登录态探测结果。</summary>
public sealed class LoginState
{
    /// <summary>是否已登录。</summary>
    public bool LoggedIn { get; set; }

    /// <summary>站点用户名。</summary>
    public string Username { get; set; } = "";

    /// <summary>站点用户 id。</summary>
    public long UserId { get; set; }

    /// <summary>**真实余额**（quota 单位——站点 /api/user/self 的 quota 字段，dashboard 页展示的就是它）。</summary>
    public long Quota { get; set; }

    /// <summary>累计已用额度（quota 单位）。</summary>
    public long UsedQuota { get; set; }

    /// <summary>请求总数。</summary>
    public long RequestCount { get; set; }

    /// <summary>账户分组。</summary>
    public string Group { get; set; } = "";

    /// <summary>失败原因（未登录时给用户看的引导语）。</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 站点会话——一次 CDP 会话内完成「登录态探测 + 分页拉取 + 口径快照」。
/// **登录由人在真实浏览器里做**：本类发现未登录即返回引导，不代填账号密码、不读 cookie。
/// **凭据不出浏览器**：access_token 只活在页面上下文里，由页面内的 fetch 使用，从不回传本进程。
/// </summary>
public sealed class SiteSession
{
    /// <summary>页面导航后的稳定等待（毫秒）——SPA 首屏要一点时间才挂上 fetch 拦截器。</summary>
    private const int NavigateSettleMs = 1800;

    /// <summary>受控浏览器中心。</summary>
    private readonly BrowserHub _hub;
    /// <summary>直连通道的 HTTP 客户端（API key 站点专用——不启浏览器）。</summary>
    private static readonly HttpClient DirectHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>构造站点会话。</summary>
    /// <param name="hub">受控浏览器中心。</param>
    public SiteSession(BrowserHub hub)
    {
        _hub = hub;
    }

    /// <summary>
    /// 探测站点登录态——必要时启动受控窗口并打开站点首页，让用户能就地登录。
    /// </summary>
    /// <param name="site">站点适配器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>登录态结果。</returns>
    public async Task<LoginState> ProbeLoginAsync(IApiSite site, CancellationToken ct)
    {
        // [段0] API key 直连站点（特批）——不经浏览器，直接用 key 调取数接口探活
        if (site is NewApiSite direct && direct.ApiKey.Length > 0)
        {
            return await ProbeDirectAsync(direct, ct).ConfigureAwait(false);
        }

        CdpSession session;

        try
        {
            session = await OpenSessionAsync(site, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new LoginState { LoggedIn = false, Message = "浏览器不可用：" + ex.Message };
        }

        await using (session)
        {
            return await ReadLoginStateAsync(session, site, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 取站点口径快照（dashboard/models 页那套：总数 / 总额度 / 总 token / 平均 RPM·TPM）。
    /// </summary>
    /// <param name="site">站点适配器。</param>
    /// <param name="startTimestamp">起始时刻（Unix 秒）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照；失败返回 null。</returns>
    public async Task<SiteSnapshot?> FetchSnapshotAsync(IApiSite site, long startTimestamp, long endTimestamp, CancellationToken ct)
    {
        CdpSession session;

        try
        {
            session = await OpenSessionAsync(site, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        await using (session)
        {
            string script = site.BuildSnapshotScript(startTimestamp, endTimestamp);
            string raw = await session.EvaluateAsync(script, true, ct).ConfigureAwait(false);
            if (raw.Length == 0)
            {
                return null;
            }

            double windowMinutes = (endTimestamp - startTimestamp) / 60.0;
            return site.ParseSnapshot(raw, windowMinutes);
        }
    }

    /// <summary>
    /// 拉取全部用量页——逐页取到最后一页；每页回执交给回调（逐页落盘，进度可见）。
    /// **全量口径 = 不带时间区间**（站点默认只给当天，带区间会漏历史）。
    /// </summary>
    /// <param name="site">站点适配器。</param>
    /// <param name="pageSize">页大小（≤100）。</param>
    /// <param name="options">拉取选项（增量追平 / 追平链长）。</param>
    /// <param name="onPage">单页回调（页码, 页结果）——回执里给出本页新增与命中数，供追平判定。</param>
    /// <param name="progress">进度提示回调（文本）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>拉取汇总（含登录态——调用方据此决定是否引导登录）。</returns>
    public async Task<FetchSummary> FetchAllAsync(
        IApiSite site,
        int pageSize,
        FetchOptions options,
        Func<int, UsagePage, PageOutcome> onPage,
        Action<string> progress,
        CancellationToken ct)
    {
        // [段0] API key 直连站点（特批）——一次请求拿全量，不经浏览器
        if (site is NewApiSite direct && direct.ApiKey.Length > 0)
        {
            return await FetchDirectAsync(direct, options, onPage, progress, ct).ConfigureAwait(false);
        }

        var summary = new FetchSummary();
        int confirmStreak = options.ConfirmStreak > 0 ? options.ConfirmStreak : 20;

        CdpSession session;
        try
        {
            session = await OpenSessionAsync(site, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            summary.Error = "浏览器不可用：" + ex.Message;
            return summary;
        }

        await using (session)
        {
            LoginState login = await ReadLoginStateAsync(session, site, ct).ConfigureAwait(false);
            summary.Balance = login.Quota;
            summary.Username = login.Username;
            if (!login.LoggedIn)
            {
                summary.NotLoggedIn = true;
                summary.Error = login.Message;
                return summary;
            }

            int page = 1;
            int total = -1;
            int streak = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                UsagePage? current = await FetchPageAsync(session, site, page, pageSize, 0, 0, ct).ConfigureAwait(false);
                if (current is null)
                {
                    summary.Error = "第 " + page + " 页拉取失败（接口未返回）";
                    return summary;
                }

                if (total < 0 && current.Total >= 0)
                {
                    total = current.Total;
                    summary.Total = total;
                }

                summary.Pages = page;

                if (current.Records.Count == 0)
                {
                    break;
                }

                PageOutcome outcome = onPage(page, current);
                summary.Added += outcome.NewCount;
                summary.Matched += outcome.MatchedCount;

                // [段1] 追平链长——本页整页命中则接续累计，否则从页尾的连续命中数重新起算
                streak = outcome.TailMatched == current.Records.Count ? streak + outcome.TailMatched : outcome.TailMatched;

                if (outcome.Stop)
                {
                    summary.Error = "入库回调中止";
                    return summary;
                }

                summary.Fetched += current.Records.Count;
                progress("已拉取第 " + page + " 页 · 累计 " + summary.Fetched + " / " + (total < 0 ? "?" : total.ToString()) +
                    " · 新增 " + summary.Added + " · 一致 " + summary.Matched);

                // [段2] 增量追平——站点列表「新的在前」，连续读到与库内完全一致的记录（默认 20 条）
                //        即认定其后都是已入库的历史，停止后续请求（省页数与刷新频控额度）
                if (options.Incremental && streak >= confirmStreak)
                {
                    summary.StoppedEarly = true;
                    progress("已追平历史（连续 " + streak + " 条与库内一致）——停止后续拉取");
                    break;
                }

                // [段3] 到底判据：本页不足一页，或已覆盖服务端报告的 total
                if (current.Records.Count < pageSize)
                {
                    break;
                }

                if (total >= 0 && summary.Fetched >= total)
                {
                    break;
                }

                page++;
            }

            // [段2] 顺带取一次站点口径快照（dashboard/models 页那套）——失败不阻断主流程，出声即可
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SiteSnapshot? snapshot = await TryFetchSnapshotAsync(session, site, now - 86400, now, ct).ConfigureAwait(false);
            summary.Snapshot = snapshot;

            summary.Ok = true;
            return summary;
        }
    }

    /// <summary>在同一会话内取口径快照（失败返回 null）。</summary>
    private static async Task<SiteSnapshot?> TryFetchSnapshotAsync(CdpSession session, IApiSite site, long startTimestamp, long endTimestamp, CancellationToken ct)
    {
        try
        {
            string script = site.BuildSnapshotScript(startTimestamp, endTimestamp);
            string raw = await session.EvaluateAsync(script, true, ct).ConfigureAwait(false);
            if (raw.Length == 0)
            {
                return null;
            }

            double windowMinutes = (endTimestamp - startTimestamp) / 60.0;
            return site.ParseSnapshot(raw, windowMinutes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 打开登录页——**在通道标签页里导航**（钩子已就位，登录响应里的 access_token 会被页面内钩子捕获并留在页面里）。
    /// 窗口是可见的，用户就在这个标签页里登录；登录完直接点「检查登录」即可。
    /// </summary>
    /// <param name="site">站点适配器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受控实例（供面板回显端口）。</returns>
    public async Task<BrowserInstance> OpenLoginAsync(IApiSite site, CancellationToken ct)
    {
        // [段0] API key 直连站点（特批）——没有浏览器登录这回事，出声拒绝（不静默什么都不做）
        if (site is NewApiSite direct && direct.ApiKey.Length > 0)
        {
            throw new InvalidOperationException("该站点走 API key 直连通道，不需要浏览器登录");
        }

        (BrowserInstance instance, _) = await _hub.EnsureAsync(site.ProfileDir, site.BaseUrl, ct).ConfigureAwait(false);

        CdpSession session = await OpenSessionAsync(site, ct).ConfigureAwait(false);
        await using (session)
        {
            await session.NavigateAsync(site.LoginUrl, ct).ConfigureAwait(false);
            await Task.Delay(NavigateSettleMs, ct).ConfigureAwait(false);
        }

        return instance;
    }

    /// <summary>
    /// 打开受控会话——确保窗口存在、连通道标签页、按需注入钩子并导航。
    /// **页面已在站点域内就不重新导航**——window 上的 token 缓存与钩子得以延续，避免重复触发站点刷新频控。
    /// </summary>
    private async Task<CdpSession> OpenSessionAsync(IApiSite site, CancellationToken ct)
    {
        // [段1] 确保受控窗口（首次打开站点首页，用户可直接登录）
        (BrowserInstance instance, bool launched) = await _hub.EnsureAsync(site.ProfileDir, site.BaseUrl, ct).ConfigureAwait(false);

        // [段2] 连通道标签页——采集 / 探测都在它里面执行，不影响用户正在看的标签页
        string socket = await _hub.EnsureChannelSocketAsync(instance, ct).ConfigureAwait(false);
        CdpSession session = await CdpSession.ConnectAsync(socket, 20000, ct).ConfigureAwait(false);

        try
        {
            await session.EnablePageAsync(ct).ConfigureAwait(false);

            // [段3] 钩子——导航前注入（文档创建时执行）；注入状态登记在受控实例上（跨进程持久，避免重复注入）
            string hook = site.BuildHookScript();
            bool hookAdded = false;
            if (hook.Length > 0 && !instance.HasHook(site.Id))
            {
                await session.AddInitScriptAsync(hook, ct).ConfigureAwait(false);
                instance.MarkHook(site.Id);
                _hub.Persist();
                hookAdded = true;
            }

            // [段4] 页面已在站点域内且钩子已就位 → 不重新导航，保住 window 上的 token 缓存；
            //       刚补注入钩子 → 必须重新加载，让钩子在文档创建时生效
            string currentUrl = await session.EvaluateAsync("String(location.href)", false, ct).ConfigureAwait(false);
            bool sameOrigin = currentUrl.StartsWith(site.BaseUrl, StringComparison.OrdinalIgnoreCase);
            if (!sameOrigin || hookAdded)
            {
                await session.NavigateAsync(site.BaseUrl, ct).ConfigureAwait(false);
                await Task.Delay(NavigateSettleMs, ct).ConfigureAwait(false);
            }
            else if (launched)
            {
                await Task.Delay(NavigateSettleMs, ct).ConfigureAwait(false);
            }

            // [段5] 会话准备（装 token 助手）——幂等，钩子可能已捕到 token
            string prep = site.BuildSessionPrepScript();
            if (prep.Length > 0)
            {
                await session.EvaluateAsync(prep, false, ct).ConfigureAwait(false);
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>在已打开的会话里读登录态。</summary>
    private async Task<LoginState> ReadLoginStateAsync(CdpSession session, IApiSite site, CancellationToken ct)
    {
        string raw = await session.EvaluateAsync(site.LoginProbeScript, true, ct).ConfigureAwait(false);
        if (raw.Length == 0)
        {
            return new LoginState { LoggedIn = false, Message = "登录态探测无返回——页面可能未加载完" };
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            bool loggedIn = root.TryGetProperty("loggedIn", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            var state = new LoginState
            {
                LoggedIn = loggedIn,
                Username = ReadText(root, "username"),
                UserId = ReadLong(root, "userId"),
                Quota = ReadLong(root, "quota"),
                UsedQuota = ReadLong(root, "usedQuota"),
                RequestCount = ReadLong(root, "requestCount"),
                Group = ReadText(root, "group"),
            };

            if (!loggedIn)
            {
                string reason = ReadText(root, "reason");
                state.Message = "未登录——请在已打开的浏览器窗口里登录 " + site.DisplayName +
                    "，然后点「重新检查」" + (reason.Length > 0 ? "（探针：" + reason + "）" : "");
            }

            return state;
        }
        catch (JsonException ex)
        {
            return new LoginState { LoggedIn = false, Message = "登录态探测返回不可解析：" + ex.Message };
        }
    }

    /// <summary>取一页——页面内用缓存的 access_token 拉取（token 只活在页面上下文）。</summary>
    private static async Task<UsagePage?> FetchPageAsync(
        CdpSession session, IApiSite site, int page, int pageSize, long startTimestamp, long endTimestamp, CancellationToken ct)
    {
        string script = site.BuildUsagePageScript(page, pageSize, startTimestamp, endTimestamp);

        // [段1] 逐次重试——SPA 首帧 / 瞬时抖动不至于让整轮失败（退避递增）
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(500 * attempt, ct).ConfigureAwait(false);
            }

            string raw = await session.EvaluateAsync(script, true, ct).ConfigureAwait(false);
            if (raw.Length == 0)
            {
                continue;
            }

            string status = "";
            string body = "";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                status = doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.ToString() : "";
                body = doc.RootElement.TryGetProperty("body", out JsonElement b) ? (b.GetString() ?? "") : "";
            }
            catch (JsonException)
            {
                continue;
            }

            if (body.Length == 0 || status == "401")
            {
                continue;
            }

            if (status != "200")
            {
                throw new InvalidOperationException("用量接口返回 HTTP " + status + "：" + Truncate(body, 200));
            }

            return site.ParseUsagePage(body);
        }

        return null;
    }

    /// <summary>读一个文本字段。</summary>
    private static string ReadText(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => "",
        };
    }

    /// <summary>读一个整数字段。</summary>
    private static long ReadLong(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long direct))
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>截断长文本（报错信息用）。</summary>
    private static string Truncate(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        return text.Substring(0, limit) + "…";
    }
    /// <summary>
    /// 直连取数——用 API key 调站点取数接口，返回原始响应体（**特批通道**，不经浏览器）。
    /// 失败一律出声（HTTP 码 + 响应片段），不静默返回空表。
    /// </summary>
    /// <param name="site">站点适配器（ApiKey 非空）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>原始 JSON 响应体。</returns>
    private static async Task<string> GetDirectAsync(NewApiSite site, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, site.BuildUsageUrl());
        request.Headers.Add("Authorization", "Bearer " + site.ApiKey);

        using HttpResponseMessage response = await DirectHttp.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string hint = (int)response.StatusCode == 429 ? "（站点限流——短时密集请求会触发，稍后再试）" : "";
            throw new InvalidOperationException("取数接口返回 HTTP " + (int)response.StatusCode + hint + "：" + Truncate(body, 200));
        }

        return body;
    }
    /// <summary>
    /// 直连探活——用 API key 调一次取数接口，能拿到记录即视为凭据有效（**特批通道**）。
    /// 该站点没有用户信息接口可用（`/api/user/self` 对 key 返回 401），真实余额与站点口径都拿不到——
    /// 故只回「凭据有效 / 无效」+ 账号名 + 本次可见记录数。
    /// </summary>
    /// <param name="site">站点适配器（ApiKey 非空）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>登录态结果（LoggedIn = 凭据可用）。</returns>
    private static async Task<LoginState> ProbeDirectAsync(NewApiSite site, CancellationToken ct)
    {
        try
        {
            string body = await GetDirectAsync(site, ct).ConfigureAwait(false);
            UsagePage page = site.ParseUsagePage(body);

            var state = new LoginState { LoggedIn = true, RequestCount = page.Records.Count, Message = "API key 有效" };
            if (page.Records.Count > 0)
            {
                using JsonDocument doc = JsonDocument.Parse(page.Records[0]);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("username", out JsonElement user) && user.ValueKind == JsonValueKind.String)
                {
                    state.Username = user.GetString() ?? "";
                }
            }

            return state;
        }
        catch (Exception ex)
        {
            return new LoginState { LoggedIn = false, Message = "API key 直连失败：" + ex.Message };
        }
    }
    /// <summary>
    /// 直连拉取——一次请求拿全量（**特批通道**）。该站点的取数接口忽略分页参数、固定返回全量，
    /// 故没有分页循环、也没有「追平提前停止」（追平省的是请求数，这里请求数恒为 1）。
    /// 落库仍走同一份 `PageWriter`——落库口径唯一实现，不因通道不同而分叉。
    /// </summary>
    /// <param name="site">站点适配器（ApiKey 非空）。</param>
    /// <param name="options">拉取选项（直连通道不适用——一次请求即全量，保留形参以对齐调用方）。</param>
    /// <param name="onPage">单页回调（页码固定 1）。</param>
    /// <param name="progress">进度提示回调（文本）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>拉取汇总。</returns>
    private static async Task<FetchSummary> FetchDirectAsync(
        NewApiSite site,
        FetchOptions options,
        Func<int, UsagePage, PageOutcome> onPage,
        Action<string> progress,
        CancellationToken ct)
    {
        var summary = new FetchSummary();

        try
        {
            progress("直连取数中（API key）…");
            string body = await GetDirectAsync(site, ct).ConfigureAwait(false);
            UsagePage page = site.ParseUsagePage(body);

            summary.Pages = 1;
            summary.Total = page.Total;
            summary.Fetched = page.Records.Count;

            if (page.Records.Count > 0)
            {
                PageOutcome outcome = onPage(1, page);
                summary.Added = outcome.NewCount;
                summary.Matched = outcome.MatchedCount;

                if (outcome.Stop)
                {
                    summary.Error = "入库回调中止";
                    return summary;
                }
            }

            progress("直连取数完成：" + page.Records.Count + " 条 · 新增 " + summary.Added + " · 一致 " + summary.Matched);
            summary.Ok = true;
            return summary;
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            return summary;
        }
    }
}

/// <summary>拉取选项。</summary>
public sealed class FetchOptions
{
    /// <summary>
    /// 增量追平——站点列表「新的在前」，连续读到 ConfirmStreak 条与库内完全一致的记录即停止后续拉取。
    /// 关掉即全量拉到底（首次入库 / 需重扫历史时用）。
    /// </summary>
    public bool Incremental { get; set; } = true;

    /// <summary>追平确认链长（默认 20）——必须连续这么多条与库内完全一致才认定已追平。</summary>
    public int ConfirmStreak { get; set; } = 20;
}

/// <summary>单页入库回执——供追平判定。</summary>
public sealed class PageOutcome
{
    /// <summary>本页新增条数（库内没有或字段有变化）。</summary>
    public int NewCount { get; set; }

    /// <summary>本页与库内完全一致的条数。</summary>
    public int MatchedCount { get; set; }

    /// <summary>本页尾部连续一致的条数（下一轮追平链从它接续）。</summary>
    public int TailMatched { get; set; }

    /// <summary>是否要求中止整轮拉取。</summary>
    public bool Stop { get; set; }
}

/// <summary>拉取汇总。</summary>
public sealed class FetchSummary
{
    /// <summary>是否成功完成。</summary>
    public bool Ok { get; set; }

    /// <summary>是否因未登录而中止。</summary>
    public bool NotLoggedIn { get; set; }

    /// <summary>拉取页数。</summary>
    public int Pages { get; set; }

    /// <summary>拉取条数。</summary>
    public int Fetched { get; set; }

    /// <summary>新增条数（库内没有或字段有变化——增量模式下通常远小于 Fetched）。</summary>
    public int Added { get; set; }

    /// <summary>与库内完全一致的条数。</summary>
    public int Matched { get; set; }

    /// <summary>是否因追平历史而提前停止（省下后续页的请求）。</summary>
    public bool StoppedEarly { get; set; }

    /// <summary>服务端报告总条数（-1 = 未知）。</summary>
    public int Total { get; set; } = -1;

    /// <summary>真实余额（quota 单位；0 = 未取到）。</summary>
    public long Balance { get; set; }

    /// <summary>站点账号名（登录成功时有值——总览站点块据此显示「已登录（账号）」）。</summary>
    public string Username { get; set; } = "";

    /// <summary>站点口径快照（null = 未取到）。</summary>
    public SiteSnapshot? Snapshot { get; set; }

    /// <summary>失败原因。</summary>
    public string Error { get; set; } = "";
}
