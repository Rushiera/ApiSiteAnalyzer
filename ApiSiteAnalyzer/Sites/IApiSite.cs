using System.Collections.Generic;

namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// API 站点适配器——把「一个 API 站的用量数据怎么取」收进一个实现类。
/// 新增站点 = 新增一个实现 + 在 config.json 里登记；上层（采集 / 面板 / 库）零改动。
/// </summary>
public interface IApiSite
{
    /// <summary>站点键（库内唯一，用作数据分区）。</summary>
    string Id { get; }

    /// <summary>站点显示名。</summary>
    string DisplayName { get; }

    /// <summary>站点根地址（不带尾斜杠）。</summary>
    string BaseUrl { get; }

    /// <summary>浏览器用户目录（登录态载体——程序不存账号密码）。</summary>
    string ProfileDir { get; }

    /// <summary>登录页地址（未登录时引导用户去这里）。</summary>
    string LoginUrl { get; }

    /// <summary>额度换算口径：每单位货币对应的 quota 数（用于把 quota 折成金额）。</summary>
    double QuotaPerUnit { get; }

    /// <summary>货币符号（展示用）。</summary>
    string CurrencySymbol { get; }

    /// <summary>登录态探针——在页面上下文里执行的 JS 源码，返回 JSON 字符串。</summary>
    string LoginProbeScript { get; }

    /// <summary>会话准备脚本——连上页面后先执行一次（如装 token 助手）；返回空串表示无需准备。</summary>
    /// <returns>JS 源码。</returns>
    string BuildSessionPrepScript();

    /// <summary>
    /// 页面钩子脚本——**导航前**注入（文档创建时执行）。
    /// 用途：在页面上下文内旁听站点自身的凭据交换，把 access_token 留在页面里（不回传本进程）。
    /// </summary>
    /// <returns>JS 源码（无钩子需求返回空串）。</returns>
    string BuildHookScript();

    /// <summary>构造「取第 page 页用量日志」的页面内 JS 源码。</summary>
    /// <param name="page">页码（1 起）。</param>
    /// <param name="pageSize">页大小。</param>
    /// <param name="startTimestamp">起始时刻（Unix 秒，0 = 不限）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒，0 = 不限）。</param>
    /// <returns>JS 源码（自带 JSON.stringify）。</returns>
    string BuildUsagePageScript(int page, int pageSize, long startTimestamp, long endTimestamp);

    /// <summary>把一页原始响应体解析成记录清单。</summary>
    /// <param name="rawBody">接口返回的原始 JSON 文本。</param>
    /// <returns>解析结果（含总条数与记录）。</returns>
    UsagePage ParseUsagePage(string rawBody);

    /// <summary>把一条记录解析成可入库的行（字段名 → 值）。</summary>
    /// <param name="record">原始记录 JSON 文本。</param>
    /// <returns>库行。</returns>
    UsageRow ParseRow(string record);

    /// <summary>
    /// 构造「取站点口径快照」的页面内 JS 源码——总数 / 总额度 / 总 token / 平均 RPM / 平均 TPM。
    /// 这些是**站点自己的聚合口径**（dashboard 页展示的那套），与本地逐条汇总未必相等。
    /// </summary>
    /// <param name="startTimestamp">起始时刻（Unix 秒，0 = 不限）。</param>
    /// <param name="endTimestamp">结束时刻（Unix 秒，0 = 不限）。</param>
    /// <returns>JS 源码（自带 JSON.stringify）。</returns>
    string BuildSnapshotScript(long startTimestamp, long endTimestamp);

    /// <summary>解析站点口径快照。</summary>
    /// <param name="rawBody">快照脚本返回的 JSON。</param>
    /// <param name="windowMinutes">统计窗口分钟数（用于折算平均 RPM / TPM）。</param>
    /// <returns>快照。</returns>
    SiteSnapshot ParseSnapshot(string rawBody, double windowMinutes);
}
