using System;
using System.Collections.Generic;
using System.Linq;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;

namespace ApiSiteAnalyzer.Web;

/// <summary>合并后的聚合行——跨站同名分组相加（额度按各站换算比折算成金额后累加）。</summary>
public sealed class MergedRow
{
    /// <summary>分组名。</summary>
    public string Name { get; set; } = "";

    /// <summary>条数。</summary>
    public long Count { get; set; }

    /// <summary>原始额度合计（各站单位不同，跨站不可比——仅作追溯）。</summary>
    public long Quota { get; set; }

    /// <summary>折算金额合计（各站 quota / 该站换算比，再相加）。</summary>
    public double Amount { get; set; }

    /// <summary>输入 token 合计。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token 合计。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>缓存 token 合计。</summary>
    public long CacheTokens { get; set; }
    /// <summary>已探明请求 ID 数（各站去重后相加）。</summary>
    public long RequestIdCount { get; set; }
    /// <summary>三种 token 之和（输入 + 输出 + 缓存）——图表的排序与显示口径。</summary>
    public long Token
    {
        get
        {
            return PromptTokens + CompletionTokens + CacheTokens;
        }
    }
}

/// <summary>总览里的一个站点块——该站的关键数据（真实余额 / 本地汇总 / 上次拉取）。</summary>
public sealed class SiteBlock
{
    /// <summary>站点键。</summary>
    public string Id { get; set; } = "";

    /// <summary>站点显示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>通道（browser = 浏览器登录态 / apikey = API key 直连）。</summary>
    public string Channel { get; set; } = "";

    /// <summary>库内该站记录总行数（不过滤记录类型）。</summary>
    public long Rows { get; set; }

    /// <summary>按当前范围过滤后的本地记录数。</summary>
    public long Count { get; set; }

    /// <summary>本地消费金额（该站 quota / 换算比）。</summary>
    public double Amount { get; set; }

    /// <summary>输入 token 合计。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token 合计。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>缓存 token 合计。</summary>
    public long CacheTokens { get; set; }

    /// <summary>缓存命中率（cache / prompt）。</summary>
    public double CacheHitRate { get; set; }

    /// <summary>最近一次探到的真实余额（quota 单位；0 = 未取到）。</summary>
    public long Balance { get; set; }

    /// <summary>该站是否不设限——无余额接口的站点按「不设限」展示，不是 0、也不是取数失败。</summary>
    public bool UnlimitedBalance { get; set; }

    /// <summary>每货币单位 quota 数（余额折算用）。</summary>
    public double QuotaPerUnit { get; set; }

    /// <summary>货币符号。</summary>
    public string CurrencySymbol { get; set; } = "";

    /// <summary>上次拉取时刻（空 = 从未拉取）。</summary>
    public string LastFetchAt { get; set; } = "";
}

/// <summary>总览（跨站合并）结果——面板「总览」视图的全部数据。</summary>
public sealed class AllSitesResult
{
    /// <summary>记录条数合计。</summary>
    public long Count { get; set; }

    /// <summary>消费金额合计（各站折算后相加）。</summary>
    public double Amount { get; set; }

    /// <summary>输入 token 合计。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token 合计。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>缓存 token 合计。</summary>
    public long CacheTokens { get; set; }

    /// <summary>已探明请求 ID 数合计（各站去重后相加）。</summary>
    public long RequestIdCount { get; set; }

    /// <summary>近十次窗口内的记录条数（跨站混合窗口）。</summary>
    public long RecentSampleCount { get; set; }

    /// <summary>近十次请求的平均首字延迟（毫秒）。</summary>
    public double RecentAvgFirstTokenMs { get; set; }

    /// <summary>近十次请求的平均耗时（秒）。</summary>
    public double RecentAvgUseTime { get; set; }

    /// <summary>近十次请求的平均输出速率（token/秒）。</summary>
    public double RecentAvgSpeedTps { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）内的记录条数——卡片对比行的样本数。</summary>
    public long PrevSampleCount { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）的平均首字延迟（毫秒）。</summary>
    public double PrevAvgFirstTokenMs { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）的平均耗时（秒）。</summary>
    public double PrevAvgUseTime { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）的平均输出速率（token/秒）。</summary>
    public double PrevAvgSpeedTps { get; set; }

    /// <summary>近 24 小时窗口统计（各站相加）。</summary>
    public WindowStat Last24h { get; set; } = new WindowStat();

    /// <summary>前 24 小时窗口统计（各站相加）——四项对比行的参照窗口。</summary>
    public WindowStat Prev24h { get; set; } = new WindowStat();
    /// <summary>近 24 小时消费金额（各站按自己的换算比折算后相加）。</summary>
    public double Last24hAmount { get; set; }
    /// <summary>前 24 小时消费金额（各站按自己的换算比折算后相加）——对比行的参照值。</summary>
    public double Prev24hAmount { get; set; }

    /// <summary>按模型（跨站合并，按 token 之和倒序）。</summary>
    public List<MergedRow> ByModel { get; set; } = new List<MergedRow>();

    /// <summary>按天（跨站合并，按日期倒序）。</summary>
    public List<MergedRow> ByDay { get; set; } = new List<MergedRow>();

    /// <summary>按小时（跨站合并，按小时升序）。</summary>
    public List<MergedRow> ByHour { get; set; } = new List<MergedRow>();

    /// <summary>按令牌（跨站合并，按 token 之和倒序）。</summary>
    public List<MergedRow> ByToken { get; set; } = new List<MergedRow>();

    /// <summary>按分组（跨站合并，按 token 之和倒序）。</summary>
    public List<MergedRow> ByGroup { get; set; } = new List<MergedRow>();

    /// <summary>跨站最近记录（按时刻倒序）。</summary>
    public List<UsageRecord> Recent { get; set; } = new List<UsageRecord>();

    /// <summary>各站点块（下拉顺序）。</summary>
    public List<SiteBlock> Sites { get; set; } = new List<SiteBlock>();

    /// <summary>货币符号（各站一致才有值；混币种留空——金额是各站折算后相加）。</summary>
    public string CurrencySymbol { get; set; } = "";
}

/// <summary>
/// 总览（跨站）聚合器——把各站的分析结果合并成一份，供面板「总览」视图使用。
/// **合并口径**：条数 / token 直接相加；金额按各站换算比折算后相加（各站 quota 单位不同，直接相加无意义）。
/// **取样边界**：各站各取前 N 条再合并取前 N 条——跨站前 N 条必在各站前 N 条之内，故不漏。
/// </summary>
public static class SiteAggregate
{
    /// <summary>每站取样的最近记录条数（合并后取前 N 条即得跨站最近 N 条）。</summary>
    private const int RecentLimit = 50;

    /// <summary>合并后「按模型」保留条数。</summary>
    private const int ModelLimit = 30;

    /// <summary>合并后「按令牌 / 按分组」保留条数。</summary>
    private const int NameLimit = 20;

    /// <summary>取站点的 API key（非直连站返回空串）。</summary>
    /// <param name="site">站点适配器。</param>
    /// <returns>API key（空 = 走浏览器通道）。</returns>
    public static string ApiKeyOf(IApiSite site)
    {
        return site is NewApiSite direct ? direct.ApiKey : "";
    }

    /// <summary>站点通道——有 API key 即 API key 直连（apikey），否则走浏览器登录态（browser）。</summary>
    /// <param name="site">站点适配器。</param>
    /// <returns>通道标签。</returns>
    public static string ChannelOf(IApiSite site)
    {
        return ApiKeyOf(site).Length > 0 ? "apikey" : "browser";
    }

    /// <summary>合并全部站点的分析结果。</summary>
    /// <param name="db">库（唯一真相）。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="types">记录类型白名单（空 = 全部）。</param>
    /// <param name="balanceOf">按站点取最近一次探到的真实余额（quota 单位）。</param>
    /// <param name="day">限定「按小时」的日期（yyyy-MM-dd；空 = 跨天累计）。</param>
    /// <returns>跨站合并结果。</returns>
    public static AllSitesResult Build(Db db, IReadOnlyList<IApiSite> sites, IReadOnlyList<int> types, Func<string, long> balanceOf, string day = "")
    {
        var result = new AllSitesResult();
        var recent = new List<UsageRecord>();
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (IApiSite site in sites)
        {
            double unit = site.QuotaPerUnit <= 0 ? 500000 : site.QuotaPerUnit;
            string channel = ChannelOf(site);
            OverviewStat overview = db.Overview(site.Id, types);

            result.Count += overview.Count;
            result.Amount += overview.Quota / unit;
            result.PromptTokens += overview.PromptTokens;
            result.CompletionTokens += overview.CompletionTokens;
            result.CacheTokens += overview.CacheTokens;
            result.RequestIdCount += overview.RequestIdCount;
            result.Last24h = Add(result.Last24h, overview.Last24h);
            result.Prev24h = Add(result.Prev24h, overview.Prev24h);
            // 窗口金额——各站按自己的换算比折算后相加（跨站 quota 单位不同，不能先加 quota 再除）
            result.Last24hAmount += overview.Last24h.Quota / unit;
            result.Prev24hAmount += overview.Prev24h.Quota / unit;
            symbols.Add(site.CurrencySymbol);

            // [段1] 分组行——各站取全量（limit=0）再合并，合并后按 token 之和倒序截断（截断放在合并之后，避免漏掉跨站前排）
            Merge(result.ByModel, db.ByModel(site.Id, types, 0), unit);
            Merge(result.ByDay, db.ByDay(site.Id, types), unit);
            Merge(result.ByHour, db.ByHour(site.Id, types, day), unit);
            Merge(result.ByToken, db.ByToken(site.Id, types, 0), unit);
            Merge(result.ByGroup, db.ByGroup(site.Id, types, 0), unit);

            recent.AddRange(db.Recent(site.Id, types, RecentLimit));

            result.Sites.Add(new SiteBlock
            {
                Id = site.Id,
                DisplayName = site.DisplayName,
                Channel = channel,
                Rows = db.Count(site.Id),
                Count = overview.Count,
                Amount = overview.Quota / unit,
                PromptTokens = overview.PromptTokens,
                CompletionTokens = overview.CompletionTokens,
                CacheTokens = overview.CacheTokens,
                CacheHitRate = overview.PromptTokens > 0 ? (double)overview.CacheTokens / overview.PromptTokens : 0,
                Balance = balanceOf(site.Id),
                UnlimitedBalance = channel == "apikey",
                QuotaPerUnit = unit,
                CurrencySymbol = site.CurrencySymbol,
                LastFetchAt = db.LastFetchAt(site.Id),
            });
        }

        // [段2] 跨站最近记录——合并排序后取前 RecentLimit 条
        recent.Sort(CompareRecent);
        result.Recent = recent.Take(RecentLimit).ToList();

        // [段3] 两个十次窗口——口径与单站一致：按时刻倒序取窗口，再对 >0 的值取平均（站点偶给 0 / 负值）
        //       近十次（1–10）与再往前十次（11–20）——后者作卡片对比行的参照
        List<UsageRecord> window = recent.Take(10).ToList();
        List<UsageRecord> prevWindow = recent.Skip(10).Take(10).ToList();
        result.RecentSampleCount = window.Count;
        result.RecentAvgFirstTokenMs = Average(window.Where(r => r.FirstTokenMs > 0).Select(r => (double)r.FirstTokenMs));
        result.RecentAvgUseTime = Average(window.Where(r => r.UseTime > 0).Select(r => (double)r.UseTime));
        result.RecentAvgSpeedTps = Average(window.Where(r => r.SpeedTps > 0).Select(r => r.SpeedTps));
        result.PrevSampleCount = prevWindow.Count;
        result.PrevAvgFirstTokenMs = Average(prevWindow.Where(r => r.FirstTokenMs > 0).Select(r => (double)r.FirstTokenMs));
        result.PrevAvgUseTime = Average(prevWindow.Where(r => r.UseTime > 0).Select(r => (double)r.UseTime));
        result.PrevAvgSpeedTps = Average(prevWindow.Where(r => r.SpeedTps > 0).Select(r => r.SpeedTps));

        // [段4] 分组行排序与截断——按天新日期在上，按小时固定 00–23 升序，其余按 token 之和倒序
        result.ByModel = Top(result.ByModel, ModelLimit);
        result.ByToken = Top(result.ByToken, NameLimit);
        result.ByGroup = Top(result.ByGroup, NameLimit);
        result.ByDay = result.ByDay.OrderByDescending(r => r.Name, StringComparer.Ordinal).ToList();
        result.ByHour = result.ByHour.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();

        // [段5] 货币符号——各站一致才标；混币种留空（金额是各站折算后相加，标单一符号会误导）
        result.CurrencySymbol = symbols.Count == 1 ? symbols.First() : "";
        return result;
    }

    /// <summary>把一站的窗口统计并入目标（各字段相加——窗口口径下跨站直接累加）。</summary>
    /// <param name="target">目标窗口。</param>
    /// <param name="one">该站窗口。</param>
    /// <returns>并入后的目标窗口。</returns>
    private static WindowStat Add(WindowStat target, WindowStat one)
    {
        target.Count += one.Count;
        target.PromptTokens += one.PromptTokens;
        target.CompletionTokens += one.CompletionTokens;
        target.CacheTokens += one.CacheTokens;
        target.RequestIdCount += one.RequestIdCount;
        return target;
    }

    /// <summary>把一站的聚合行并入目标表（同名相加，额度按该站换算比折算成金额）。</summary>
    /// <param name="target">目标表。</param>
    /// <param name="rows">该站的聚合行。</param>
    /// <param name="unit">该站每货币单位 quota 数。</param>
    private static void Merge(List<MergedRow> target, List<AggregateRow> rows, double unit)
    {
        foreach (AggregateRow row in rows)
        {
            MergedRow? hit = null;
            foreach (MergedRow item in target)
            {
                if (string.Equals(item.Name, row.Name, StringComparison.Ordinal))
                {
                    hit = item;
                    break;
                }
            }

            if (hit is null)
            {
                target.Add(new MergedRow
                {
                    Name = row.Name,
                    Count = row.Count,
                    Quota = row.Quota,
                    Amount = row.Quota / unit,
                    PromptTokens = row.PromptTokens,
                    CompletionTokens = row.CompletionTokens,
                    CacheTokens = row.CacheTokens,
                    RequestIdCount = row.RequestIdCount,
                });
                continue;
            }

            hit.Count += row.Count;
            hit.Quota += row.Quota;
            hit.Amount += row.Quota / unit;
            hit.PromptTokens += row.PromptTokens;
            hit.CompletionTokens += row.CompletionTokens;
            hit.CacheTokens += row.CacheTokens;
            hit.RequestIdCount += row.RequestIdCount;
        }
    }

    /// <summary>最近记录排序——时刻倒序，其次站点展示序号，再以站点键 / 稳定主键收口（跨站合并需要全序，否则同刻行次序不定）。</summary>
    /// <param name="left">左。</param>
    /// <param name="right">右。</param>
    /// <returns>比较结果。</returns>
    private static int CompareRecent(UsageRecord left, UsageRecord right)
    {
        int byTime = right.CreatedAt.CompareTo(left.CreatedAt);
        if (byTime != 0)
        {
            return byTime;
        }

        int byRemote = right.RemoteId.CompareTo(left.RemoteId);
        if (byRemote != 0)
        {
            return byRemote;
        }

        int bySite = string.CompareOrdinal(left.SiteId, right.SiteId);
        if (bySite != 0)
        {
            return bySite;
        }

        return string.CompareOrdinal(left.LogKey, right.LogKey);
    }

    /// <summary>按 token 之和（输入 + 输出 + 缓存）倒序取前 N 条。</summary>
    /// <param name="rows">合并后的行。</param>
    /// <param name="limit">保留条数。</param>
    /// <returns>截断后的行。</returns>
    private static List<MergedRow> Top(List<MergedRow> rows, int limit)
    {
        return rows.OrderByDescending(r => r.Token).Take(limit).ToList();
    }

    /// <summary>求平均（空序列返回 0——与 SQL 侧 COALESCE(AVG(...),0) 口径一致）。</summary>
    /// <param name="values">值序列。</param>
    /// <returns>平均值。</returns>
    private static double Average(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count > 0 ? list.Average() : 0;
    }
}
