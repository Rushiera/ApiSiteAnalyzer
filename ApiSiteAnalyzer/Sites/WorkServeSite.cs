using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// 本机 work 壳（serve）站点适配器——第四站的取数通道。
/// <para>
/// 数据源：<c>GET {baseUrl}/api/usage/records?days=&amp;limit=&amp;offset=</c>（work 壳的**明细台账**，
/// 与它自己的控制台「用量」页读同一份 <c>UsageStore</c>——两个用量界面因此同源，页面可以不同）。
/// </para>
/// <para>
/// 三条边界：① 本站没有登录面也没有凭据（本机回环地址，<see cref="AlwaysDirect"/> 恒 true，不进浏览器）；
/// ② <b>字段名即跨项目契约</b>——壳侧 <c>UsageStore.DetailRow</c> 改字段必须同时改本文件的
/// <see cref="ParseRow"/>，反之亦然；③ 金额口径 <c>积分 / 200 = 金额</c>：壳给的 <c>credit</c> 按
/// <c>QuotaPerUnit / 200</c> 折成 quota，展示时再 <c>quota / QuotaPerUnit</c> 还原成金额。
/// </para>
/// </summary>
public sealed class WorkServeSite : IPagedDirectSite
{
    /// <summary>积分折金额的分母——壳的 credit 每 200 点 = 1 元。</summary>
    public const double CreditPerCurrency = 200.0;

    /// <summary>站点键。</summary>
    private readonly string _id;

    /// <summary>站点显示名。</summary>
    private readonly string _displayName;

    /// <summary>站点根地址（本机 serve，如 http://127.0.0.1:18100）。</summary>
    private readonly string _baseUrl;

    /// <summary>浏览器用户目录占位（本站走直连，不启浏览器）。</summary>
    private readonly string _profileDir;

    /// <summary>每货币单位 quota 数。</summary>
    private readonly double _quotaPerUnit;

    /// <summary>货币符号。</summary>
    private readonly string _currencySymbol;

    /// <summary>构造适配器。</summary>
    /// <param name="id">站点键。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="baseUrl">站点根地址。</param>
    /// <param name="profileDir">浏览器用户目录（占位）。</param>
    /// <param name="quotaPerUnit">每货币单位 quota 数。</param>
    /// <param name="currencySymbol">货币符号。</param>
    public WorkServeSite(string id, string displayName, string baseUrl, string profileDir, double quotaPerUnit, string currencySymbol)
    {
        _id = id;
        _displayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
        _profileDir = profileDir;
        _quotaPerUnit = quotaPerUnit <= 0 ? 2000000 : quotaPerUnit;
        _currencySymbol = currencySymbol;
    }

    /// <summary>站点键（库内唯一，数据分区键）。</summary>
    public string Id => _id;

    /// <summary>站点显示名。</summary>
    public string DisplayName => _displayName;

    /// <summary>站点根地址（不带尾斜杠）。</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>浏览器用户目录（本站不用浏览器，占位）。</summary>
    public string ProfileDir => _profileDir;

    /// <summary>登录页地址——本机 serve 的索引页。</summary>
    public string LoginUrl => _baseUrl;

    /// <summary>每货币单位 quota 数。</summary>
    public double QuotaPerUnit => _quotaPerUnit;

    /// <summary>货币符号。</summary>
    public string CurrencySymbol => _currencySymbol;

    /// <summary>余额位名称——本站没有余额概念，展示台账累计已用积分。</summary>
    public string BalanceLabel => "已用积分";

    /// <summary>余额位单位——积分（不折成货币显示）。</summary>
    public string BalanceUnit => "积分";

    /// <summary>余额位显示除数——库内 quota = 积分 × 10000（积分/200 = 元，quotaPerUnit = 2000000），故除以 10000 得积分。</summary>
    public double BalanceScale => 10000;

    /// <summary>余额位取本地台账累计（壳不提供余额，只有已用）。</summary>
    public bool BalanceFromLedger => true;

    /// <summary>凭据——本站不需要，恒空。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>恒走直连——本机 serve 没有浏览器登录面。</summary>
    public bool AlwaysDirect => true;

    /// <summary>单页条数——与壳侧 <c>limit</c> 上限 2000 对齐。</summary>
    public int PageSize => 1000;

    /// <summary>侦察候选端点——本机 serve 的用量三面。</summary>
    public IReadOnlyList<string> ProbePaths => new[]
    {
        "/api/usage/stats",
        "/api/usage/records",
        "/api/usage/ids",
    };

    /// <summary>登录态探针——本站无登录面，恒视为可用。</summary>
    public string LoginProbeScript => @"JSON.stringify({ loggedIn: true, reason: '本机 serve，无登录面' })";

    /// <summary>会话准备脚本——直连通道，无页面。</summary>
    /// <returns>空串。</returns>
    public string BuildSessionPrepScript()
    {
        return "";
    }

    /// <summary>页面钩子脚本——直连通道，无页面。</summary>
    /// <returns>空串。</returns>
    public string BuildHookScript()
    {
        return "";
    }

    /// <summary>用量页脚本——本站只有直连通道（无浏览器面）。</summary>
    /// <param name="page">页码（不使用）。</param>
    /// <param name="pageSize">页大小（不使用）。</param>
    /// <param name="startTimestamp">起始时刻（不使用）。</param>
    /// <param name="endTimestamp">结束时刻（不使用）。</param>
    /// <returns>501 回执（让采集链出声失败，不静默返回空表）。</returns>
    public string BuildUsagePageScript(int page, int pageSize, long startTimestamp, long endTimestamp)
    {
        return @"JSON.stringify({ status: 501, body: '本站为本机 work 壳，仅支持直连通道' })";
    }

    /// <summary>全量取数 URL（第一页）——<see cref="IDirectSite"/> 的最小口径。</summary>
    /// <returns>完整 URL。</returns>
    public string BuildUsageUrl()
    {
        return BuildUsageUrl(0, PageSize);
    }

    /// <summary>
    /// 分页取数 URL——<c>days=0</c> 取满保留期（壳侧保留 60 天）。
    /// </summary>
    /// <param name="offset">已取条数。</param>
    /// <param name="limit">本页条数。</param>
    /// <returns>完整 URL。</returns>
    public string BuildUsageUrl(int offset, int limit)
    {
        int size = limit <= 0 ? PageSize : limit;
        return _baseUrl + "/api/usage/records?days=0&limit=" + size.ToString(CultureInfo.InvariantCulture)
            + "&offset=" + (offset < 0 ? 0 : offset).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 解析一页明细——壳侧回执形如 <c>{ok,days,total,offset,limit,nextOffset,rows[]}</c>。
    /// </summary>
    /// <param name="rawBody">原始 JSON。</param>
    /// <returns>页结果（Total = 窗口全集条数）。</returns>
    public UsagePage ParseUsagePage(string rawBody)
    {
        var page = new UsagePage();

        using JsonDocument doc = JsonDocument.Parse(rawBody);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("work serve 明细回执不是对象：" + rawBody.Substring(0, Math.Min(200, rawBody.Length)));
        }

        JsonElement rows;
        if (!root.TryGetProperty("rows", out rows) || rows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("work serve 明细回执缺 rows 数组");
        }

        JsonElement total;
        if (root.TryGetProperty("total", out total) && total.ValueKind == JsonValueKind.Number)
        {
            page.Total = total.GetInt32();
        }
        else
        {
            page.Total = rows.GetArrayLength();
        }

        foreach (JsonElement row in rows.EnumerateArray())
        {
            page.Records.Add(row.GetRawText());
        }

        return page;
    }

    /// <summary>
    /// 把一条壳明细转成库行——**跨项目契约的唯一映射点**（壳侧 <c>DetailRow</c> ↔ 本方法）。
    /// </summary>
    /// <param name="record">原始记录 JSON。</param>
    /// <returns>库行。</returns>
    public UsageRow ParseRow(string record)
    {
        using JsonDocument doc = JsonDocument.Parse(record);
        JsonElement o = doc.RootElement;

        var row = new UsageRow();

        string requestId = Str(o, "requestId");
        long atMs = Long(o, "atMs");
        string model = Str(o, "model");
        long elapsedMs = Long(o, "elapsedMs");

        row.LogKey = requestId.Length > 0 ? requestId : ("legacy:" + atMs + ":" + model + ":" + elapsedMs);
        row.RemoteId = 0;
        row.CreatedAt = atMs / 1000L;
        row.Type = 2;
        row.ModelName = model;
        row.UpstreamModel = Str(o, "upstreamModel");
        row.TokenName = Str(o, "keyName");
        row.Group = Str(o, "source");

        // 金额口径：壳给 credit，积分/200 = 元 → quota = credit × (QuotaPerUnit / 200)
        JsonElement credit;
        if (o.TryGetProperty("credit", out credit) && credit.ValueKind == JsonValueKind.Number)
        {
            double c = credit.GetDouble();
            row.Quota = (long)Math.Round(c * (_quotaPerUnit / CreditPerCurrency), MidpointRounding.AwayFromZero);
        }

        row.PromptTokens = Long(o, "prompt");
        row.CompletionTokens = Long(o, "completion");
        row.CacheTokens = Long(o, "cache");
        row.FirstTokenMs = Long(o, "firstMs");
        row.UseTime = (long)Math.Round(elapsedMs / 1000.0, MidpointRounding.AwayFromZero);
        row.SpeedTps = elapsedMs > 0
            ? Math.Round(row.CompletionTokens / (elapsedMs / 1000.0), 2)
            : 0;
        row.IsStream = Bool(o, "stream");
        row.Channel = 0;
        row.RequestId = requestId;
        row.Content = Str(o, "message");
        row.ReasoningEffort = Str(o, "effort");

        string conv = Str(o, "upstreamConversationId");
        if (conv.Length == 0)
        {
            conv = Str(o, "conversationId");
        }
        row.SessionId = conv;

        row.Ok = Bool(o, "ok");
        row.Status = Long(o, "status");
        row.ErrorKind = Str(o, "errorKind");
        row.Realm = Str(o, "realm");
        row.Uid = Str(o, "uid");
        row.Nickname = Str(o, "nickname");
        row.ConvSource = Str(o, "convSource");
        row.SameRetries = Long(o, "sameRetries");
        row.FallbackFrom = Str(o, "fallbackFrom");

        return row;
    }

    /// <summary>账户标识——本机 serve 用壳的版本口径，不暴露账号。</summary>
    /// <param name="body">取数回执原文（不使用）。</param>
    /// <returns>固定文本。</returns>
    public string DescribeAccount(string body)
    {
        return "本机 work 壳";
    }

    /// <summary>站点口径快照脚本——直连通道不使用。</summary>
    /// <param name="startTimestamp">起始时刻（不使用）。</param>
    /// <param name="endTimestamp">结束时刻（不使用）。</param>
    /// <returns>501 回执。</returns>
    public string BuildSnapshotScript(long startTimestamp, long endTimestamp)
    {
        return @"JSON.stringify({ status: 501, body: '本站为本机 work 壳，仅支持直连通道' })";
    }

    /// <summary>解析站点口径快照——直连通道不在页面里跑脚本，空回执。</summary>
    /// <param name="rawBody">回执原文。</param>
    /// <param name="windowMinutes">统计窗口分钟数。</param>
    /// <returns>空快照。</returns>
    public SiteSnapshot ParseSnapshot(string rawBody, double windowMinutes)
    {
        return new SiteSnapshot { FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
    }

    /// <summary>读字符串字段（缺失 / 非字符串 → 空串）。</summary>
    private static string Str(JsonElement o, string name)
    {
        JsonElement v;
        if (!o.TryGetProperty(name, out v))
        {
            return "";
        }

        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
    }

    /// <summary>读整数字段（缺失 / 非数字 → 0）。</summary>
    private static long Long(JsonElement o, string name)
    {
        JsonElement v;
        if (!o.TryGetProperty(name, out v) || v.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        long parsed;
        return v.TryGetInt64(out parsed) ? parsed : (long)v.GetDouble();
    }

    /// <summary>读布尔字段（缺失 → false）。</summary>
    private static bool Bool(JsonElement o, string name)
    {
        JsonElement v;
        if (!o.TryGetProperty(name, out v))
        {
            return false;
        }

        if (v.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (v.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return false;
    }
}
