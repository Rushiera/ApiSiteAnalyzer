using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// New API（QuantumNous/new-api）站点适配器——本站（api.xyjun.fun）实测口径：
/// 登录：POST /api/user/login（本工具不用——登录由用户在真实浏览器里做）
/// 余额：GET /api/user/self → data.quota（**真实余额**）
/// 用量：GET /api/log/self?p=N&amp;page_size=100（须带 Authorization: Bearer &lt;access_token&gt;）
/// 口径快照：GET /api/data/self（按小时聚合）→ 前端汇总出总数 / 总额度 / 总 token / 平均 RPM·TPM
/// 分页：p 1 起、page_size 上限 100、排序 logs.id desc（新的在前）
/// </summary>
public sealed class NewApiSite : IApiSite
{
    /// <summary>站点键。</summary>
    private readonly string _id;

    /// <summary>站点显示名。</summary>
    private readonly string _displayName;

    /// <summary>站点根地址。</summary>
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
    public NewApiSite(string id, string displayName, string baseUrl, string profileDir, double quotaPerUnit, string currencySymbol)
    {
        _id = id;
        _displayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
        _profileDir = profileDir;
        _quotaPerUnit = quotaPerUnit <= 0 ? 500000 : quotaPerUnit;
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

    /// <summary>登录页地址。</summary>
    public string LoginUrl => _baseUrl + "/login";

    /// <summary>每货币单位对应的 quota 数（本站 500000）。</summary>
    public double QuotaPerUnit => _quotaPerUnit;

    /// <summary>货币符号。</summary>
    public string CurrencySymbol => _currencySymbol;

    /// <summary>
    /// 登录态探针——在页面上下文内取 access_token，再读账户信息（含**真实余额** quota）。
    /// **凭据不出浏览器**：token 由页面内的 fetch 使用，从不回传本进程。
    /// 站点口径（实测）：面板接口认 Authorization: Bearer；cookie 只管刷新（/api/user/auth/refresh）。
    /// </summary>
    public string LoginProbeScript => @"(async function(){
  async function once(force){
    var t = await window.__asaEnsureToken(force);
    if (!t) { return { status: 401, body: '' }; }
    var r = await fetch('/api/user/self', { credentials: 'include', headers: { 'Accept': 'application/json', 'Authorization': 'Bearer ' + t } });
    return { status: r.status, body: await r.text() };
  }
  try {
    var res = await once(false);
    if (res.status === 401) { res = await once(true); }
    if (res.status !== 200) { return JSON.stringify({ loggedIn: false, reason: 'http-' + res.status }); }
    var j = JSON.parse(res.body);
    if (!j || !j.success) { return JSON.stringify({ loggedIn: false, reason: 'api-false' }); }
    var u = j.data || {};
    return JSON.stringify({ loggedIn: true, username: u.username || '', userId: u.id || 0,
      quota: u.quota || 0, usedQuota: u.used_quota || 0, requestCount: u.request_count || 0, group: u.group || '' });
  } catch (e) {
    return JSON.stringify({ loggedIn: false, reason: String(e) });
  }
})()";

    /// <summary>
    /// 页面内的 token 助手——刷新一次即缓存（**只活在页面上下文里，不回传本进程**）；
    /// 站点对刷新接口有频控（实测 429 + retry-after），故只刷一次、按需重刷，并遵守冷却。
    /// </summary>
    private const string TokenHelperScript = @"(function(){
  if (window.__asaEnsureToken) { return 'ready'; }
  if (window.__asaToken === undefined || window.__asaToken === null) { window.__asaToken = ''; }
  window.__asaCooldownUntil = 0;
  window.__asaEnsureToken = async function(force){
    if (window.__asaToken && !force) { return window.__asaToken; }
    if (Date.now() < window.__asaCooldownUntil) { return ''; }
    var rf = await fetch('/api/user/auth/refresh', { method: 'POST', credentials: 'include' });
    if (rf.status === 429) {
      var ra = parseInt(rf.headers.get('retry-after') || '60', 10);
      window.__asaCooldownUntil = Date.now() + (isNaN(ra) ? 60 : ra) * 1000;
      window.__asaToken = '';
      return '';
    }
    if (rf.status !== 200) { window.__asaToken = ''; return ''; }
    var rj = await rf.json();
    var t = (rj && rj.data) ? rj.data.access_token : '';
    window.__asaToken = t || '';
    return window.__asaToken;
  };
  return 'ready';
})()";

    /// <summary>
    /// 页面钩子（导航前注入）——旁听站点自身的凭据交换，把 access_token 留在页面里：
    /// ① 拦 fetch / XHR 的 Authorization 头（站点前端自己带的就是它）；
    /// ② 拦刷新 / 登录接口的响应体。
    /// **token 只活在页面上下文，从不回传本进程**。
    /// </summary>
    public string BuildHookScript()
    {
        return @"(function(){
  if (window.__asaHooked) { return; }
  window.__asaHooked = true;
  if (!window.__asaToken) { window.__asaToken = ''; }
  function remember(v){
    if (!v) { return; }
    var s = String(v).replace(/^Bearer\s+/i, '').trim();
    if (s.length > 20) { window.__asaToken = s; }
  }
  // [段1] fetch 通道
  var of = window.fetch;
  window.fetch = function(input, init){
    try {
      var url = (typeof input === 'string') ? input : ((input && input.url) || '');
      if (init && init.headers) {
        var h = init.headers;
        if (typeof h.get === 'function') { remember(h.get('Authorization')); }
        else if (h['Authorization']) { remember(h['Authorization']); }
        else if (h['authorization']) { remember(h['authorization']); }
      }
      var p = of.apply(this, arguments);
      if (url.indexOf('/api/user/auth/refresh') >= 0 || url.indexOf('/api/user/login') >= 0) {
        p.then(function(resp){
          try { resp.clone().json().then(function(j){ if (j && j.data) { remember(j.data.access_token); } }).catch(function(){}); } catch (e) {}
        }).catch(function(){});
      }
      return p;
    } catch (e) {
      return of.apply(this, arguments);
    }
  };
  // [段2] XHR 通道（axios 默认适配器）
  var oo = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function(method, url){
    try { this.__asaUrl = String(url); } catch (e) {}
    return oo.apply(this, arguments);
  };
  var oh = XMLHttpRequest.prototype.setRequestHeader;
  XMLHttpRequest.prototype.setRequestHeader = function(name, value){
    try { if (String(name).toLowerCase() === 'authorization') { remember(value); } } catch (e) {}
    return oh.apply(this, arguments);
  };
  var os = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.send = function(){
    var self = this;
    try {
      if (self.__asaUrl && (self.__asaUrl.indexOf('/api/user/auth/refresh') >= 0 || self.__asaUrl.indexOf('/api/user/login') >= 0)) {
        self.addEventListener('load', function(){
          try { var j = JSON.parse(self.responseText || '{}'); if (j && j.data) { remember(j.data.access_token); } } catch (e) {}
        });
      }
    } catch (e) {}
    return os.apply(this, arguments);
  };
})();";
    }

    /// <summary>装 token 助手（每次连上页面后先调一次）。</summary>
    /// <returns>JS 源码。</returns>
    public string BuildSessionPrepScript()
    {
        return TokenHelperScript;
    }

    /// <summary>
    /// 取第 page 页——页面内用缓存的 access_token 拉取；401 时强制刷新一次再重试。
    /// 时间区间为空时传 0（不带该参数 = 全量）。
    /// </summary>
    /// <param name="page">页码（1 起）。</param>
    /// <param name="pageSize">页大小（≤100）。</param>
    /// <param name="startTimestamp">起始时刻（Unix 秒，0 = 不限）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒，0 = 不限）。</param>
    /// <returns>JS 源码。</returns>
    public string BuildUsagePageScript(int page, int pageSize, long startTimestamp, long endTimestamp)
    {
        string url = "/api/log/self?p=" + page + "&page_size=" + pageSize;
        if (startTimestamp > 0)
        {
            url += "&start_timestamp=" + startTimestamp;
        }
        if (endTimestamp > 0)
        {
            url += "&end_timestamp=" + endTimestamp;
        }

        return "(async function(){" +
            "var url = '" + url + "';" +
            "async function once(force){" +
            "  var t = await window.__asaEnsureToken(force);" +
            "  if (!t) { return { status: 401, body: '' }; }" +
            "  var r = await fetch(url, { credentials: 'include', headers: { 'Accept': 'application/json', 'Authorization': 'Bearer ' + t } });" +
            "  return { status: r.status, body: await r.text() };" +
            "}" +
            "var res = await once(false);" +
            "if (res.status === 401) { res = await once(true); }" +
            "return JSON.stringify(res);" +
            "})()";
    }

    /// <summary>
    /// 站点口径快照脚本——取 /api/data/self（按小时聚合），汇总出总数 / 总额度 / 总 token。
    /// 这与 dashboard/models 页展示的是**同一套口径**（该页即由此接口的前端汇总得出）。
    /// </summary>
    /// <param name="startTimestamp">起始时刻（Unix 秒，0 = 不限）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒，0 = 不限）。</param>
    /// <returns>JS 源码。</returns>
    public string BuildSnapshotScript(long startTimestamp, long endTimestamp)
    {
        return "(async function(){" +
            "async function once(force){" +
            "  var t = await window.__asaEnsureToken(force);" +
            "  if (!t) { return { status: 401, body: '' }; }" +
            "  var url = '/api/data/self?start_timestamp=" + startTimestamp + "&end_timestamp=" + endTimestamp + "&default_time=hour';" +
            "  var r = await fetch(url, { credentials: 'include', headers: { 'Accept': 'application/json', 'Authorization': 'Bearer ' + t } });" +
            "  return { status: r.status, body: await r.text() };" +
            "}" +
            "var res = await once(false);" +
            "if (res.status === 401) { res = await once(true); }" +
            "if (res.status !== 200) { return JSON.stringify({ ok: false, reason: 'http-' + res.status }); }" +
            "var j = JSON.parse(res.body);" +
            "if (!j || !j.success || !j.data) { return JSON.stringify({ ok: false, reason: 'api-false' }); }" +
            "var count = 0, quota = 0, tokens = 0;" +
            "for (var i = 0; i < j.data.length; i++) {" +
            "  count += (j.data[i].count || 0);" +
            "  quota += (j.data[i].quota || 0);" +
            "  tokens += (j.data[i].token_used || 0);" +
            "}" +
            "return JSON.stringify({ ok: true, count: count, quota: quota, tokens: tokens, buckets: j.data.length });" +
            "})()";
    }

    /// <summary>解析站点口径快照（RPM / TPM 由窗口分钟数折算——与站点前端同法）。</summary>
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
        snapshot.TotalQuota = ReadLong(root, "quota");
        snapshot.TotalTokens = ReadLong(root, "tokens");

        if (windowMinutes > 0)
        {
            snapshot.AvgRpm = snapshot.TotalCount / windowMinutes;
            snapshot.AvgTpm = snapshot.TotalTokens / windowMinutes;
        }

        return snapshot;
    }

    /// <summary>解析一页用量响应——校验 success，取 total 与 items。</summary>
    /// <param name="rawBody">接口返回的原始 JSON 文本。</param>
    /// <returns>解析结果。</returns>
    public UsagePage ParseUsagePage(string rawBody)
    {
        var page = new UsagePage();

        using JsonDocument doc = JsonDocument.Parse(rawBody);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("用量接口返回 success=false：" + Truncate(rawBody, 300));
        }

        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("用量接口返回结构异常（缺 data）");
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

    /// <summary>解析一条记录——主字段 + other 内嵌字段（缓存 token / 首字延迟 / 上游模型）。</summary>
    /// <param name="record">原始记录 JSON 文本。</param>
    /// <returns>库行。</returns>
    public UsageRow ParseRow(string record)
    {
        var row = new UsageRow();

        using JsonDocument doc = JsonDocument.Parse(record);
        JsonElement root = doc.RootElement;

        row.RemoteId = ReadLong(root, "id");
        row.CreatedAt = ReadLong(root, "created_at");
        row.Type = (int)ReadLong(root, "type");
        row.ModelName = ReadText(root, "model_name");
        row.TokenName = ReadText(root, "token_name");
        row.Group = ReadText(root, "group");
        row.Quota = ReadLong(root, "quota");
        row.PromptTokens = ReadLong(root, "prompt_tokens");
        row.CompletionTokens = ReadLong(root, "completion_tokens");
        row.UseTime = ReadLong(root, "use_time");
        row.IsStream = root.TryGetProperty("is_stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True;
        row.Channel = ReadLong(root, "channel");
        row.RequestId = ReadText(root, "request_id");
        row.Content = ReadText(root, "content");

        // [段1] other 内嵌字段——缓存 token / 首字延迟 / 上游模型；结构随版本变，缺项留 0 或空
        if (root.TryGetProperty("other", out JsonElement other) && other.ValueKind == JsonValueKind.String)
        {
            string raw = other.GetString() ?? "";
            if (raw.Length > 0 && raw.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using JsonDocument inner = JsonDocument.Parse(raw);
                    JsonElement obj = inner.RootElement;
                    row.CacheTokens = ReadLong(obj, "cache_tokens");
                    row.FirstTokenMs = ReadLong(obj, "frt");
                    row.UpstreamModel = ReadText(obj, "upstream_model_name");
                }
                catch (JsonException)
                {
                    // other 不是合法 JSON——按缺项处理，不影响主字段
                }
            }
        }

        // [段2] 稳定主键——站点返回的 id 是**展示序号**（每次查询从 1 重排），拿它当键会导致整库错位更新。
        // request_id 是站点真实的请求标识（唯一且稳定），优先用它；缺失时退化为「时刻+模型+用量」组合键。
        row.LogKey = row.RequestId.Length > 0
            ? row.RequestId
            : "c" + row.CreatedAt.ToString(CultureInfo.InvariantCulture) +
              "|" + row.ModelName +
              "|" + row.PromptTokens.ToString(CultureInfo.InvariantCulture) +
              "|" + row.CompletionTokens.ToString(CultureInfo.InvariantCulture) +
              "|" + row.Quota.ToString(CultureInfo.InvariantCulture);

        // [段3] 输出速率（t/s）——站点前端口径 = completion_tokens / use_time（实测逐条对上）
        if (row.UseTime > 0)
        {
            row.SpeedTps = (double)row.CompletionTokens / row.UseTime;
        }

        return row;
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

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long parsed))
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
