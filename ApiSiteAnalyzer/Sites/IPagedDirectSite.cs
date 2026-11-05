namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// 分页直连站点——本机常驻服务类站点（work 壳的 serve）一次给不完全量：
/// 取数口按 <c>offset</c> / <c>limit</c> 翻页。其余语义与 <see cref="IDirectSite"/> 一致——
/// 不进浏览器、不需要凭据（<see cref="IApiSite.ProfileDir"/> 只作占位）。
/// </summary>
public interface IPagedDirectSite : IDirectSite
{
    /// <summary>单页条数（服务端可能夹取上限；实际生效值以回执 <c>limit</c> 为准）。</summary>
    int PageSize { get; }

    /// <summary>
    /// 构造某一段的完整取数 URL。
    /// </summary>
    /// <param name="offset">已取条数（0 起）。</param>
    /// <param name="limit">本页条数。</param>
    /// <returns>完整 URL。</returns>
    string BuildUsageUrl(int offset, int limit);
}
