using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// 「知序空间」（buddy.mewbius.com）站点适配器——**非 New API 系**，自有一套接口与凭据体系。
/// 前端：Vue3 + Vite SPA；接口基址 `/api/v1`；响应包装 `{code,message,data}`，`code=0` 为成功。
/// 认证：**localStorage JWT**（`auth_token` / `refresh_token` / `token_expires_at`）→ `Authorization: Bearer`；
///       401 后 `POST /api/v1/auth/refresh`（body `{refresh_token}`）续期。
/// 用量：`GET /api/v1/usage?page=N&amp;page_size=M`（可选 `start_date` / `end_date`，`yyyy-MM-dd`）→ `data.items` + `data.total`。
/// 开销：`actual_cost` **已是金额**（元，含峰谷倍率）——本适配器按 `QuotaPerUnit` 折成整数 quota 落库，
///       金额 = quota / QuotaPerUnit，无需二次换算。
/// **凭据不出浏览器**：access_token 只在页面上下文里被读取与使用，从不回传本进程、从不落盘。
/// </summary>
public sealed class BuddySite : IApiSite
{
    /// <summary>接口基址（站点根 + /api/v1）。</summary>
    private const string ApiPrefix = "/api/v1";

    /// <summary>站点键。</summary>
    private readonly string _id;

    /// <summary>站点显示名。</summary>
    private readonly string _displayName;

    /// <summary>站点根地址（不带尾斜杠）。</summary>
    private readonly string _baseUrl;

    /// <summary>浏览器用户目录。</summary>
    private readonly string _profileDir;

    /// <summary>额度换算（每货币单位 quota 数）。</summary>
    private readonly double _quotaPerUnit;

    /// <summary>货币符号。</summary>
    private readonly string _currencySymbol;

    /// <summary>构造适配器。</summary>
    /// <param name="id">站点键。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="baseUrl">站点根地址。</param>
    /// <param name="profileDir">浏览器用户目录。</param>
    /// <param name="quotaPerUnit">每货币单位 quota 数。</param>
    /// <param name="currencySymbol">货币符号。</param>
    public BuddySite(string id, string displayName, string baseUrl, string profileDir, double quotaPerUnit, string currencySymbol)
    {
        _id = id;
        _displayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
        _profileDir = profileDir;
        _quotaPerUnit = quotaPerUnit <= 0 ? 1000000 : quotaPerUnit;
        _currencySymbol = currencySymbol;
    }

    /// <summary>站点键（库内唯一，数据分区键）。</summary>
    public string Id => _id;

    /// <summary>站点显示名。</summary>
    public string DisplayName => _displayName;

    /// <summary>站点根地址（不带尾斜杠）。</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>浏览器用户目录（登录态载体——程序不存账号密码）。</summary>
    public string ProfileDir => _profileDir;

    /// <summary>登录页地址（前端路由 `/login`——首页是兑换页，不是登录入口）。</summary>
    public string LoginUrl => _baseUrl + "/login";

    /// <summary>每货币单位对应的 quota 数（本站 100 0000 = 1 元，即 quota 单位为微元，精度到 1e-6 元）。</summary>
    public double QuotaPerUnit => _quotaPerUnit;

    /// <summary>货币符号。</summary>
    public string CurrencySymbol => _currencySymbol;

    /// <summary>
    /// 登录态探针——在页面上下文内取 localStorage 的 JWT，再读账户信息（含**真实余额** balance）。
    /// **凭据不出浏览器**：token 由页面内的 fetch 使用，从不回传本进程。
    /// </summary>
    public string LoginProbeScript => @"(async function(){
  async function once(force){
    var t = await window.__asaEnsureToken(force);
    if (!t) { return { status: 401, body: '' }; }
    var r = await fetch('/api/v1/user/profile', { credentials: 'include', signal: AbortSignal.timeout(15000), headers: { 'Accept': 'application/json', 'Accept-Language': 'zh-CN', 'Authorization': 'Bearer ' + t } });
    return { status: r.status, body: await r.text() };
  }
  try {
    var res = await once(false);
    if (res.status === 401) { res = await once(true); }
    if (res.status !== 200) { return JSON.stringify({ loggedIn: false, reason: 'http-' + res.status }); }
    var j = JSON.parse(res.body);
    if (!j || j.code !== 0 || !j.data) { return JSON.stringify({ loggedIn: false, reason: 'api-false' }); }
    var u = j.data;
    var bal = Number(u.balance || 0);
    /* [段1] 用量汇总——/user/profile 不带请求数与消费额，另取一次站点口径（全时段）补齐面板要的那两行 */
    var count = 0, used = 0;
    try {
      var t2 = await window.__asaEnsureToken(false);
      var rs = await fetch('/api/v1/usage/stats', { credentials: 'include', signal: AbortSignal.timeout(15000), headers: { 'Accept': 'application/json', 'Accept-Language': 'zh-CN', 'Authorization': 'Bearer ' + t2 } });
      if (rs.status === 200) {
        var sj = await rs.json();
        if (sj && sj.code === 0 && sj.data) {
          count = Number(sj.data.total_requests || 0);
          used = Number(sj.data.total_actual_cost || sj.data.total_cost || 0);
        }
      }
    } catch (e) {}
    return JSON.stringify({ loggedIn: true,
      username: u.username || u.name || u.nickname || u.email || ('#' + (u.id || 0)),
      userId: u.id || 0,
      quota: Math.round(bal * 1000000),
      usedQuota: Math.round(used * 1000000),
      requestCount: count,
      group: u.group || u.plan || '',
      raw: JSON.stringify(u).slice(0, 800) });
  } catch (e) {
    return JSON.stringify({ loggedIn: false, reason: String(e) });
  }
})()";

    /// <summary>
    /// 侦察候选端点——本站前端源码里出现过的全部接口（新站接入时一次问清「有哪些数据可取」）。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> ProbePaths => new[]
    {
        "/api/v1/user/profile",
        "/api/v1/usage?page=1&page_size=5",
        "/api/v1/usage/stats",
        "/api/v1/usage/errors",
        "/api/v1/usage/dashboard/stats",
        "/api/v1/usage/dashboard/trend",
        "/api/v1/usage/dashboard/models",
        "/api/v1/keys?page=1&page_size=5",
        "/api/v1/groups/available",
        "/api/v1/subscriptions",
        "/api/v1/redeem/history?page=1&page_size=5",
        "/api/v1/user/aff",
        "/api/v1/settings/public",
    };

    /// <summary>
    /// 页面内的 token 助手——token 直接取自 localStorage（站点自己的凭据存储，同源可读）；
    /// 缺失或强制时调一次 `/auth/refresh` 续期并回写四键。**只活在页面上下文，不回传本进程**。
    /// </summary>
    private const string TokenHelperScript = @"(function(){
  if (window.__asaEnsureToken) { return 'ready'; }
  window.__asaRefreshInflight = null;
  window.__asaEnsureToken = async function(force){
    function read(){
      try { return localStorage.getItem('auth_token') || ''; } catch (e) { return ''; }
    }
    if (!force) {
      var t = read();
      if (t) { return t; }
    }
    if (window.__asaRefreshInflight) { return window.__asaRefreshInflight; }
    window.__asaRefreshInflight = (async function(){
      try {
        var rt = localStorage.getItem('refresh_token') || '';
        if (!rt) { return ''; }
        var r = await fetch('/api/v1/auth/refresh', { method: 'POST', credentials: 'include', signal: AbortSignal.timeout(15000),
          headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' }, body: JSON.stringify({ refresh_token: rt }) });
        if (r.status !== 200) { return ''; }
        var j = await r.json();
        if (!j || j.code !== 0 || !j.data) { return ''; }
        var d = j.data;
        var at = d.access_token || '';
        if (at) {
          try {
            localStorage.setItem('auth_token', at);
            if (d.refresh_token) { localStorage.setItem('refresh_token', d.refresh_token); }
            if (d.expires_in) { localStorage.setItem('token_expires_at', String(Date.now() + Number(d.expires_in) * 1000)); }
          } catch (e) {}
        }
        return at;
      } catch (e) {
        return '';
      } finally {
        window.__asaRefreshInflight = null;
      }
    })();
    return window.__asaRefreshInflight;
  };
  return 'ready';
})()";

    /// <summary>
    /// 页面钩子——本站不需要：凭据在 localStorage（同源直接可读），没有需要旁听的凭据交换。
    /// </summary>
    /// <returns>空串（不注入）。</returns>
    public string BuildHookScript()
    {
        return "";
    }

    /// <summary>装 token 助手（每次连上页面后先调一次）。</summary>
    /// <returns>JS 源码。</returns>
    public string BuildSessionPrepScript()
    {
        return TokenHelperScript;
    }

    /// <summary>
    /// 取第 page 页——页面内用 localStorage 的 JWT 拉取；401 时刷新一次再重试。
    /// 时间区间为空时传 0（不带该参数 = 站点默认窗口）。
    /// </summary>
    /// <param name="page">页码（1 起）。</param>
    /// <param name="pageSize">页大小。</param>
    /// <param name="startTimestamp">起始时刻（Unix 秒，0 = 不限）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒，0 = 不限）。</param>
    /// <returns>JS 源码。</returns>
    public string BuildUsagePageScript(int page, int pageSize, long startTimestamp, long endTimestamp)
    {
        string url = ApiPrefix + "/usage?page=" + page + "&page_size=" + pageSize;
        if (startTimestamp > 0)
        {
            url += "&start_date=" + ToDate(startTimestamp);
        }
        if (endTimestamp > 0)
        {
            url += "&end_date=" + ToDate(endTimestamp);
        }

        return "(async function(){" +
            "var url = '" + url + "';" +
            "async function once(force){" +
            "  var t = await window.__asaEnsureToken(force);" +
            "  if (!t) { return { status: 401, body: '' }; }" +
            "  var r = await fetch(url, { credentials: 'include', signal: AbortSignal.timeout(15000), headers: { 'Accept': 'application/json', 'Accept-Language': 'zh-CN', 'Authorization': 'Bearer ' + t } });" +
            "  return { status: r.status, body: await r.text() };" +
            "}" +
            "var res = await once(false);" +
            "if (res.status === 401) { res = await once(true); }" +
            "return JSON.stringify(res);" +
            "})()";
    }

    /// <summary>
    /// 站点口径快照脚本——取 `/usage/stats`（该站 dashboard 汇总接口）：
    /// `total_requests` / `total_tokens` / `total_actual_cost`。
    /// </summary>
    /// <param name="startTimestamp">起始时刻（Unix 秒，0 = 不限）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒，0 = 不限）。</param>
    /// <returns>JS 源码。</returns>
    public string BuildSnapshotScript(long startTimestamp, long endTimestamp)
    {
        string url = ApiPrefix + "/usage/stats";
        if (startTimestamp > 0)
        {
            url += "?start_date=" + ToDate(startTimestamp);
            if (endTimestamp > 0)
            {
                url += "&end_date=" + ToDate(endTimestamp);
            }
        }

        return "(async function(){" +
            "var url = '" + url + "';" +
            "async function once(force){" +
            "  var t = await window.__asaEnsureToken(force);" +
            "  if (!t) { return { status: 401, body: '' }; }" +
            "  var r = await fetch(url, { credentials: 'include', signal: AbortSignal.timeout(15000), headers: { 'Accept': 'application/json', 'Accept-Language': 'zh-CN', 'Authorization': 'Bearer ' + t } });" +
            "  return { status: r.status, body: await r.text() };" +
            "}" +
            "var res = await once(false);" +
            "if (res.status === 401) { res = await once(true); }" +
            "if (res.status !== 200) { return JSON.stringify({ ok: false, reason: 'http-' + res.status }); }" +
            "var j = JSON.parse(res.body);" +
            "if (!j || j.code !== 0 || !j.data) { return JSON.stringify({ ok: false, reason: 'api-false' }); }" +
            "var d = j.data;" +
            "return JSON.stringify({ ok: true, count: Number(d.total_requests || 0), cost: Number(d.total_actual_cost || 0), tokens: Number(d.total_tokens || 0) });" +
            "})()";
    }

    /// <summary>解析站点口径快照（RPM / TPM 由窗口分钟数折算）。</summary>
    /// <param name="rawBody">快照脚本的返回 JSON。</param>
    /// <param name="windowMinutes">统计窗口分钟数（用于折算平均 RPM / TPM）。</param>
    /// <returns>快照。</returns>
    public SiteSnapshot ParseSnapshot(string rawBody, double windowMinutes)
    {
        var snapshot = new SiteSnapshot { FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };

        using JsonDocument doc = JsonDocument.Parse(rawBody);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("口径快照取数失败：" + Truncate(rawBody, 200));
        }

        snapshot.TotalCount = ReadLong(root, "count");
        snapshot.TotalQuota = (long)Math.Round(ReadDouble(root, "cost") * _quotaPerUnit, MidpointRounding.AwayFromZero);
        snapshot.TotalTokens = ReadLong(root, "tokens");

        if (windowMinutes > 0)
        {
            snapshot.AvgRpm = snapshot.TotalCount / windowMinutes;
            snapshot.AvgTpm = snapshot.TotalTokens / windowMinutes;
        }

        return snapshot;
    }

    /// <summary>解析一页用量响应——校验 `code=0`，取 `data.items` 与 `data.total`。</summary>
    /// <param name="rawBody">接口返回的原始 JSON 文本。</param>
    /// <returns>解析结果。</returns>
    public UsagePage ParseUsagePage(string rawBody)
    {
        var page = new UsagePage();

        using JsonDocument doc = JsonDocument.Parse(rawBody);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("用量接口返回结构异常（根不是对象）：" + Truncate(rawBody, 300));
        }

        if (!root.TryGetProperty("code", out JsonElement code) || !IsZero(code))
        {
            throw new InvalidOperationException("用量接口返回 code≠0：" + Truncate(rawBody, 300));
        }

        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("用量接口返回结构异常（缺 data 对象）");
        }

        if (data.TryGetProperty("total", out JsonElement total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out int totalCount))
        {
            page.Total = totalCount;
        }

        if (data.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                page.Records.Add(item.GetRawText());
            }
        }

        return page;
    }

    /// <summary>
    /// 解析一条记录——**实测样本**（2026-10-17）字段：
    /// `id` / `request_id`（`client:&lt;uuid&gt;`，稳定唯一）/ `created_at`（ISO8601 带时区）/
    /// `model` / `reasoning_effort` / `inbound_endpoint` / `group_id` / `subscription_id` /
    /// `input_tokens` / `output_tokens` / `cache_read_tokens` / `cache_creation_tokens` /
    /// `total_cost` / `actual_cost`（元，含倍率）/ `duration_ms` / `first_token_ms` /
    /// `request_type` / `stream` / `api_key.name` / `group.name`。
    /// </summary>
    /// <param name="record">原始记录 JSON 文本。</param>
    /// <returns>库行。</returns>
    public UsageRow ParseRow(string record)
    {
        var row = new UsageRow();

        using JsonDocument doc = JsonDocument.Parse(record);
        JsonElement root = doc.RootElement;

        row.RemoteId = ReadLong(root, "id");
        row.CreatedAt = ReadTimestamp(root, "created_at");
        row.ModelName = ReadText(root, "model");
        row.TokenName = ReadKeyName(root);
        row.Group = ReadNestedName(root, "group", "name");
        row.RequestId = ReadText(root, "request_id");
        row.ReasoningEffort = ReadText(root, "reasoning_effort");
        row.SessionId = ReadText(root, "session_id");
        row.IsStream = string.Equals(ReadText(root, "request_type"), "stream", StringComparison.OrdinalIgnoreCase)
            || (root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True);

        // [段1] 金额——actual_cost 已是该站最终金额（元，含倍率与峰谷）；按 QuotaPerUnit 折成整数 quota 落库
        double cost = ReadDouble(root, "actual_cost");
        if (cost <= 0)
        {
            cost = ReadDouble(root, "total_cost");
        }

        row.Quota = (long)Math.Round(cost * _quotaPerUnit, MidpointRounding.AwayFromZero);

        // [段2] token——本站 input_tokens **不含**缓存（缓存单列 read / creation）；
        //       本工具口径 prompt = 含缓存的输入总量 → prompt = 输入 + 缓存读 + 缓存写，cache = 缓存读（命中部分）。
        //       命中率 = cache / prompt 与站点前端同法（其分子分母即 cache_read / (input + cache_read + cache_creation)）。
        long input = ReadLong(root, "input_tokens");
        long cacheRead = ReadLong(root, "cache_read_tokens");
        long cacheWrite = ReadLong(root, "cache_creation_tokens");
        row.PromptTokens = input + cacheRead + cacheWrite;
        row.CompletionTokens = ReadLong(root, "output_tokens");
        row.CacheTokens = cacheRead;

        // [段3] 记录类型——本站记录表只收用量（成功消费）与失败请求；无 New API 的 type 码位，映射为
        //       2 = 消费 / 5 = 错误（有 status_code ≥ 400 或 error_message 时判错误）
        long statusCode = ReadLong(root, "status_code");
        row.Content = ReadText(root, "error_message");
        row.Type = statusCode >= 400 || row.Content.Length > 0 ? 5 : 2;

        // [段4] 耗时——本站给**毫秒**（duration_ms / first_token_ms）；库内 use_time 列是秒，
        //       按四舍五入折成秒（毫秒精度由 first_token_ms 保留；速率复算与库内值同源）
        long durationMs = ReadLong(root, "duration_ms");
        row.UseTime = durationMs > 0 ? (long)Math.Round(durationMs / 1000.0, MidpointRounding.AwayFromZero) : 0;
        row.FirstTokenMs = ReadLong(root, "first_token_ms");

        // [段5] 稳定主键——request_id（`client:<uuid>`）实测存在且唯一；缺失时退化为记录 id；再缺则组合键
        row.LogKey = row.RequestId.Length > 0
            ? row.RequestId
            : (row.RemoteId > 0
                ? "b" + row.RemoteId.ToString(CultureInfo.InvariantCulture)
                : "c" + row.CreatedAt.ToString(CultureInfo.InvariantCulture) +
                  "|" + row.ModelName +
                  "|" + row.PromptTokens.ToString(CultureInfo.InvariantCulture) +
                  "|" + row.CompletionTokens.ToString(CultureInfo.InvariantCulture) +
                  "|" + row.Quota.ToString(CultureInfo.InvariantCulture));

        // [段6] 输出速率（t/s）——与 New API 站同口径：completion / use_time（用库内同一份秒值，可逐条复算）
        if (row.UseTime > 0)
        {
            row.SpeedTps = (double)row.CompletionTokens / row.UseTime;
        }

        return row;
    }

    /// <summary>取嵌套对象里的名字（如 `group.name`）；平铺形态取 `group_name`。</summary>
    private static string ReadNestedName(JsonElement root, string objectName, string fieldName)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(objectName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
        {
            string name = ReadText(nested, fieldName);
            if (name.Length > 0)
            {
                return name;
            }
        }

        string flat = ReadText(root, objectName + "_" + fieldName);
        return flat.Length > 0 ? flat : ReadText(root, objectName + "_id");
    }

    /// <summary>取记录里的密钥名——`api_key.name`（对象形态）或 `api_key_name` / `key_name`（平铺形态）。</summary>
    private static string ReadKeyName(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("api_key", out JsonElement key))
        {
            if (key.ValueKind == JsonValueKind.Object)
            {
                string name = ReadText(key, "name");
                if (name.Length > 0)
                {
                    return name;
                }
            }
            else if (key.ValueKind == JsonValueKind.String)
            {
                return key.GetString() ?? "";
            }
        }

        string flat = ReadText(root, "api_key_name");
        return flat.Length > 0 ? flat : ReadText(root, "key_name");
    }

    /// <summary>
    /// 读时刻字段（Unix 秒）——本站给 ISO8601 带时区字符串（`2026-10-17T22:36:03.327837+08:00`）；
    /// 兼容 Unix 秒 / 毫秒数字形态（毫秒按 &amp;gt; 1e11 判据折算成秒）。
    /// </summary>
    private static long ReadTimestamp(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
        {
            return numeric > 100000000000L ? numeric / 1000 : numeric;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? "";
            if (text.Length == 0)
            {
                return 0;
            }

            if (long.TryParse(text, out long fromText))
            {
                return fromText > 100000000000L ? fromText / 1000 : fromText;
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            {
                return parsed.ToUnixTimeSeconds();
            }
        }

        return 0;
    }

    /// <summary>`code` 字段是否等于 0（数字或数字字符串）。</summary>
    private static bool IsZero(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
        {
            return numeric == 0;
        }

        return value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "0", StringComparison.Ordinal);
    }

    /// <summary>把 Unix 秒转成本站窗口参数用的 `yyyy-MM-dd`（本地时区）。</summary>
    private static string ToDate(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>读一个整数字段（数字 / 数字字符串兼容）。</summary>
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

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double asDouble))
        {
            return (long)Math.Round(asDouble, MidpointRounding.AwayFromZero);
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>读一个浮点字段（数字 / 数字字符串兼容）。</summary>
    private static double ReadDouble(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double direct))
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>读一个文本字段（数字按原样转文本）。</summary>
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
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
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
}
