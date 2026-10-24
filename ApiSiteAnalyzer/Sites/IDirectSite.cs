namespace ApiSiteAnalyzer.Sites;

/// <summary>
/// API key 直连站点——不经浏览器的取数通道契约。
/// 有 key 即走直连（探活 / 拉取 / 通道标签全部据此分派）；没配 key 的站点照常走浏览器通道。
/// 该通道是**特批例外**：仅限没有「登录归人」路径可走的站点（无账号密码体系 / 全部功能只认一把 key）。
/// </summary>
public interface IDirectSite : IApiSite
{
    /// <summary>
    /// API key 凭据——空 = 走浏览器通道（默认，凭据不出浏览器）；非空 = 走 API key 直连。
    /// key 落 `data/keys.json`（不入仓），由面板站点设置里的明文条编辑。
    /// </summary>
    string ApiKey { get; set; }

    /// <summary>直连取数地址（完整 URL，含该站自己的认证形态——查询参数或请求头由调用方补齐）。</summary>
    /// <returns>完整取数 URL。</returns>
    string BuildUsageUrl();

    /// <summary>
    /// 从取数响应里读账户标识（登录态回执显示用——如令牌名 / 用户名）。
    /// </summary>
    /// <param name="body">取数接口返回的原始 JSON 文本。</param>
    /// <returns>账户标识；空 = 本站不提供。</returns>
    string DescribeAccount(string body);
}
