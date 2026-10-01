using System.Collections.Generic;

namespace ApiSiteAnalyzer.Sites;

/// <summary>一页用量响应的解析结果。</summary>
public sealed class UsagePage
{
    /// <summary>服务端报告的总条数（-1 = 未提供）。</summary>
    public int Total { get; set; } = -1;

    /// <summary>本页原始记录（JSON 文本，逐条保留——解析失败可事后重跑）。</summary>
    public List<string> Records { get; set; } = new List<string>();
}

/// <summary>一条用量记录解析出的库行。</summary>
public sealed class UsageRow
{
    /// <summary>稳定主键（本站取 request_id——站点返回的 id 是展示序号，会随查询重排）。</summary>
    public string LogKey { get; set; } = "";

    /// <summary>站点展示序号（仅参考，不作键）。</summary>
    public long RemoteId { get; set; }

    /// <summary>发生时刻（Unix 秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>记录类型（New API：1 充值 / 2 消费 / 3 管理 / 4 系统 / 5 错误 / 6 退款 / 7 登录）。</summary>
    public int Type { get; set; }

    /// <summary>模型名。</summary>
    public string ModelName { get; set; } = "";

    /// <summary>令牌名。</summary>
    public string TokenName { get; set; } = "";

    /// <summary>分组名。</summary>
    public string Group { get; set; } = "";

    /// <summary>计费额度（quota 单位）。</summary>
    public long Quota { get; set; }

    /// <summary>输入 token 数。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token 数。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>命中缓存的输入 token 数（0 = 未提供）。</summary>
    public long CacheTokens { get; set; }

    /// <summary>耗时（秒）。</summary>
    public long UseTime { get; set; }

    /// <summary>首字延迟（毫秒，0 = 未提供）。</summary>
    public long FirstTokenMs { get; set; }

    /// <summary>
    /// 输出速率（token/秒）——**本工具自算列**。
    /// 口径实测复算：站点前端的 `xx t/s` = completion_tokens / use_time（四舍五入）。
    /// </summary>
    public double SpeedTps { get; set; }

    /// <summary>是否流式。</summary>
    public bool IsStream { get; set; }

    /// <summary>渠道 id。</summary>
    public long Channel { get; set; }

    /// <summary>上游模型名（模型映射后的实际模型）。</summary>
    public string UpstreamModel { get; set; } = "";

    /// <summary>请求 id。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>记录正文（错误 / 管理类记录用）。</summary>
    public string Content { get; set; } = "";
}

/// <summary>
/// 站点口径快照——来自站点聚合接口（dashboard/models 页展示的那套数字）。
/// 与本地逐条汇总**未必相等**（站点按自己的聚合口径算），故单列一行展示。
/// </summary>
public sealed class SiteSnapshot
{
    /// <summary>总数（调用次数）。</summary>
    public long TotalCount { get; set; }

    /// <summary>总额度（quota 单位）。</summary>
    public long TotalQuota { get; set; }

    /// <summary>总 token 数。</summary>
    public long TotalTokens { get; set; }

    /// <summary>平均 RPM（按统计窗口折算）。</summary>
    public double AvgRpm { get; set; }

    /// <summary>平均 TPM（按统计窗口折算）。</summary>
    public double AvgTpm { get; set; }

    /// <summary>抓取时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string FetchedAt { get; set; } = "";
}
