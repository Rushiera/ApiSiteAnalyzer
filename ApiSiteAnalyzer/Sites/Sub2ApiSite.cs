using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// sub2api 网关站点适配器（time.api.freeshare.tech）——原 New API 站 47.250.160.225:8443 已迁移到本网关，
/// 取数面与 New API 完全不同（旧站已下线：`/api/status` 返回 410）：
/// 账户 + 聚合 + 最近调用：`GET /api/overview?key=&lt;API Key&gt;`（站点自建门户端点，key 走查询参数）
/// 按天 / 按模型 / 限额统计：`GET /v1/usage`（Bearer API Key）
/// **没有逐条全量日志接口**：旧站的 `/api/log/token` 随站下线；网关用户面板的 `/api/v1/usage` 要 JWT 登录
/// （本站是卡密体系，没有账号密码可登）。故逐条记录只能取门户给的最新 20 条，靠自动采集滚动追平——
/// 两次采集之间超过 20 次调用会漏采，且站点不提供漏采判据（2026-10-24 实测）。
/// </summary>
public sealed class Sub2ApiSite : IDirectSite
{
    /// <summary>站点键。</summary>
    private readonly string _id;

    /// <summary>站点显示名。</summary>
    private readonly string _displayName;

    /// <summary>站点根地址。</summary>
    private readonly string _baseUrl;

    /// <summary>浏览器用户目录（本站无登录面，保留字段以对齐契约）。</summary>
    private readonly string _profileDir;

    /// <summary>额度换算（每货币单位 quota 数——沿用本站历史口径 1 元 = 5000000，保证新旧记录同单位可比）。</summary>
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
    public Sub2ApiSite(string id, string displayName, string baseUrl, string profileDir, double quotaPerUnit, string currencySymbol)
    {
        _id = id;
        _displayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
        _profileDir = profileDir;
        _quotaPerUnit = quotaPerUnit <= 0 ? 5000000 : quotaPerUnit;
        _currencySymbol = currencySymbol;
    }

    /// <summary>站点键（库内唯一，数据分区键）。</summary>
    public string Id => _id;

    /// <summary>站点显示名。</summary>
    public string DisplayName => _displayName;

    /// <summary>站点根地址（不带尾斜杠）。</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>浏览器用户目录（本站无登录面）。</summary>
    public string ProfileDir => _profileDir;

    /// <summary>登录页地址——本站没有登录面，给门户首页。</summary>
    public string LoginUrl => _baseUrl;

    /// <summary>每货币单位对应的 quota 数（本站沿用 5000000——与本站历史记录同单位）。</summary>
    public double QuotaPerUnit => _quotaPerUnit;

    /// <summary>货币符号。</summary>
    public string CurrencySymbol => _currencySymbol;

    /// <summary>余额位名称——本站没有账户余额（额度按天重置），展示「今日剩余额度」。</summary>
    public string BalanceLabel => "今日剩余额度";

    /// <summary>余额位单位——空 = 货币（按 `QuotaPerUnit` 折算后配货币符号显示）。</summary>
    public string BalanceUnit => "";

    /// <summary>余额位显示除数——货币站恒为 1。</summary>
    public double BalanceScale => 1;

    /// <summary>API key 直连凭据（**特批通道**——本站为卡密体系，无账号密码可登，只认这一把 key）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>最近一次解析到的令牌名（记录本身不带，取账户信息补——按令牌聚合才不至于出现空行）。</summary>
    private string _keyName = "";

    /// <summary>最近一次解析到的分组名（同上）。</summary>
    private string _groupName = "";

    /// <summary>侦察候选端点——本站无浏览器会话可侦察，置空。</summary>
    public IReadOnlyList<string> ProbePaths => Array.Empty<string>();

    /// <summary>
    /// 登录态探针——本站没有登录面（卡密开通即得 key），如实说明而不是伪装成已登录。
    /// </summary>
    public string LoginProbeScript => @"JSON.stringify({ loggedIn: false, reason: '本站为卡密体系，无账号登录面——请在站点设置里填 API key' })";

    /// <summary>会话准备脚本——本站走直连通道，不需要页面内助手。</summary>
    /// <returns>空串。</returns>
    public string BuildSessionPrepScript()
    {
        return "";
    }

    /// <summary>页面钩子脚本——本站不走浏览器通道，无钩子需求。</summary>
    /// <returns>空串。</returns>
    public string BuildHookScript()
    {
        return "";
    }

    /// <summary>
    /// 用量页脚本——本站只有直连通道（key 不能写进页面脚本，等于把凭据交给页面）。
    /// 返 501 让采集链出声失败，而不是静默返回空表。
    /// </summary>
    /// <param name="page">页码（本实现不使用）。</param>
    /// <param name="pageSize">页大小（本实现不使用）。</param>
    /// <param name="startTimestamp">起始时刻（本实现不使用）。</param>
    /// <param name="endTimestamp">结束时刻（本实现不使用）。</param>
    /// <returns>JS 源码。</returns>
    public string BuildUsagePageScript(int page, int pageSize, long startTimestamp, long endTimestamp)
    {
        return @"JSON.stringify({ status: 501, body: '本站为 sub2api 网关，仅支持 API key 直连通道' })";
    }

    /// <summary>口径快照脚本——同用量页，走直连；浏览器通道一律 501。</summary>
    /// <param name="startTimestamp">起始时刻（本实现不使用）。</param>
    /// <param name="endTimestamp">结束时刻（本实现不使用）。</param>
    /// <returns>JS 源码。</returns>
    public string BuildSnapshotScript(long startTimestamp, long endTimestamp)
    {
        return @"JSON.stringify({ status: 501, body: '' })";
    }

    /// <summary>
    /// 直连取数地址——门户端点 `GET /api/overview?key=&lt;key&gt;`（实测 2026-10-24：只认 key 查询参数）。
    /// 参数面已实测：`n` / `limit` / `page` / `page_size` / `logs` 一律被忽略——固定返回最新 20 条。
    /// </summary>
    /// <returns>完整取数 URL。</returns>
    public string BuildUsageUrl()
    {
        return _baseUrl + "/api/overview?key=" + Uri.EscapeDataString(ApiKey);
    }

    /// <summary>从取数响应里读账户标识（令牌名 + key 尾号）。</summary>
    /// <param name="body">取数接口返回的原始 JSON 文本。</param>
    /// <returns>账户标识；解析失败返回空串。</returns>
    public string DescribeAccount(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            if (!data.TryGetProperty("key", out JsonElement key) || key.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            string name = ReadText(key, "key_name");
            string tail = ReadText(key, "key_tail");
            if (name.Length > 0 && tail.Length > 0)
            {
                return name + "（" + tail + "）";
            }

            return name.Length > 0 ? name : tail;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// 解析一次取数响应——结构 `{ ok, data: { key, usage, logs: [...] } }`。
    /// 余额位取「今日额度剩余」（`daily_limit_usd - daily_usage_usd`；日限 0 = 不设上限 → 不报余额）。
    /// </summary>
    /// <param name="rawBody">接口返回的原始 JSON 文本。</param>
    /// <returns>解析结果（Total = 本次可见条数——站点不给服务端总数）。</returns>
    public UsagePage ParseUsagePage(string rawBody)
    {
        var page = new UsagePage();

        using JsonDocument doc = JsonDocument.Parse(rawBody);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("取数接口返回 ok!=true：" + Truncate(rawBody, 300));
        }

        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("取数接口返回结构异常（缺 data）");
        }

        // [段1] 余额位——今日额度剩余（日限 0 = 不限额，不报余额）；同时记下令牌名 / 分组名（记录里没有这两项）
        if (data.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.Object)
        {
            _keyName = ReadText(key, "key_name");
            _groupName = ReadText(key, "group_name");

            double limit = ReadDouble(key, "daily_limit_usd");
            double used = ReadDouble(key, "daily_usage_usd");
            if (limit > 0)
            {
                page.BalanceQuota = (long)Math.Round((limit - used) * _quotaPerUnit, MidpointRounding.AwayFromZero);
            }
        }

        // [段2] 逐条日志——站点固定给最新 20 条，无分页
        if (data.TryGetProperty("logs", out JsonElement logs) && logs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in logs.EnumerateArray())
            {
                page.Records.Add(item.GetRawText());
            }
        }

        page.Total = page.Records.Count;
        return page;
    }

    /// <summary>
    /// 解析一条记录——字段：time（`MM-dd HH:mm:ss`，无年份）/ model / tokens_in / tokens_out /
    /// cost（货币）/ duration_ms / ttft_ms / stream。**无请求 id、无缓存 token、无上游模型**。
    /// </summary>
    /// <param name="record">原始记录 JSON 文本。</param>
    /// <returns>库行。</returns>
    public UsageRow ParseRow(string record)
    {
        var row = new UsageRow();

        using JsonDocument doc = JsonDocument.Parse(record);
        JsonElement root = doc.RootElement;

        string time = ReadText(root, "time");
        row.CreatedAt = ParseLogTime(time);
        row.Type = 2; // 2 = 消费（沿用 New API 的类型口径）
        row.ModelName = ReadText(root, "model");

        // [段0] 令牌名 / 分组名——记录本身不带，取同一次响应里账户信息（ParseUsagePage 已记下；
        //        落库调用序恒为 ParseUsagePage → ParseRow，见 PageWriter）
        row.TokenName = _keyName;
        row.Group = _groupName;
        row.PromptTokens = ReadLong(root, "tokens_in");
        row.CompletionTokens = ReadLong(root, "tokens_out");
        row.FirstTokenMs = ReadLong(root, "ttft_ms");
        row.IsStream = root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True;

        // [段1] 金额——站点给的是货币金额（美元），按 QuotaPerUnit 折成 quota 整数落库
        double cost = ReadDouble(root, "cost");
        row.Quota = (long)Math.Round(cost * _quotaPerUnit, MidpointRounding.AwayFromZero);

        // [段2] 耗时——站点给毫秒；库内 use_time 口径是秒，speed_tps 直接用毫秒算（避免秒级截断失真）
        long durationMs = ReadLong(root, "duration_ms");
        row.UseTime = durationMs > 0
            ? (long)Math.Round(durationMs / 1000.0, MidpointRounding.AwayFromZero)
            : 0;
        if (durationMs > 0)
        {
            row.SpeedTps = row.CompletionTokens * 1000.0 / durationMs;
        }

        // [段3] 稳定主键——站点不给请求 id，用「时刻 + 模型 + 两向 token + 耗时」合成；
        //        同一行重复拉取恒得同一键（幂等），同秒同值同耗时的两次调用会被并成一条（站点数据本身无法区分）
        row.LogKey = "s2|" + time +
            "|" + row.ModelName +
            "|" + row.PromptTokens.ToString(CultureInfo.InvariantCulture) +
            "|" + row.CompletionTokens.ToString(CultureInfo.InvariantCulture) +
            "|" + durationMs.ToString(CultureInfo.InvariantCulture);

        return row;
    }

    /// <summary>解析站点口径快照——本站快照走 `/v1/usage` 聚合，浏览器通道不提供，返回空快照。</summary>
    /// <param name="rawBody">快照脚本返回的 JSON。</param>
    /// <param name="windowMinutes">统计窗口分钟数。</param>
    /// <returns>空快照。</returns>
    public SiteSnapshot ParseSnapshot(string rawBody, double windowMinutes)
    {
        return new SiteSnapshot();
    }

    /// <summary>
    /// 解析站点时刻——`MM-dd HH:mm:ss`（无年份）：按当年解释；解出的时刻若在未来两天之后，
    /// 说明是去年的记录，回退一年（跨年边界）。
    /// </summary>
    /// <param name="text">站点时刻文本。</param>
    /// <returns>Unix 秒；解析失败返回 0。</returns>
    private static long ParseLogTime(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (!DateTime.TryParseExact(text, "MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            {
                return 0;
            }
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Local);
        if (local > DateTime.Now.AddDays(2))
        {
            local = local.AddYears(-1);
        }

        return new DateTimeOffset(local).ToUnixTimeSeconds();
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

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
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
