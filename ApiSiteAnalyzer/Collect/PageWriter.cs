using System.Collections.Generic;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;

namespace ApiSiteAnalyzer.Collect;

/// <summary>
/// 单页落库器——把「一页原始记录」转成库行、比对库内既有行、幂等写入，并给出追平判定回执。
/// **唯一实现**：CLI（`fetch`）与面板（`/api/fetch`）共用同一条落库口径——两处各写一份必然漂移。
/// </summary>
public static class PageWriter
{
    /// <summary>把一条原始记录转成库行。</summary>
    /// <param name="site">站点适配器。</param>
    /// <param name="record">原始记录 JSON 文本。</param>
    /// <returns>库行。</returns>
    public static UsageRecord ToRecord(IApiSite site, string record)
    {
        UsageRow row = site.ParseRow(record);
        return new UsageRecord
        {
            SiteId = site.Id,
            LogKey = row.LogKey,
            RemoteId = row.RemoteId,
            CreatedAt = row.CreatedAt,
            Type = row.Type,
            ModelName = row.ModelName,
            TokenName = row.TokenName,
            GroupName = row.Group,
            Quota = row.Quota,
            PromptTokens = row.PromptTokens,
            CompletionTokens = row.CompletionTokens,
            CacheTokens = row.CacheTokens,
            UseTime = row.UseTime,
            FirstTokenMs = row.FirstTokenMs,
            SpeedTps = row.SpeedTps,
            IsStream = row.IsStream,
            Channel = row.Channel,
            UpstreamModel = row.UpstreamModel,
            RequestId = row.RequestId,
            Content = row.Content,
        };
    }

    /// <summary>
    /// 落一页——先比对库内既有行（增量追平判据），再幂等写入。
    /// 比对必须在写入**之前**：写入后旧行已被新值覆盖，"是否与库内一致"就再也问不出来了。
    /// </summary>
    /// <param name="db">库。</param>
    /// <param name="site">站点适配器。</param>
    /// <param name="page">一页原始记录。</param>
    /// <returns>入库回执（新增 / 一致 / 页尾连续一致）。</returns>
    public static PageOutcome Write(Db db, IApiSite site, UsagePage page)
    {
        var buffer = new List<UsageRecord>();
        foreach (string record in page.Records)
        {
            buffer.Add(ToRecord(site, record));
        }

        List<bool> known = db.MatchExisting(buffer);
        db.Upsert(buffer);

        int matched = 0;
        foreach (bool hit in known)
        {
            if (hit)
            {
                matched++;
            }
        }

        int tail = 0;
        for (int i = known.Count - 1; i >= 0; i--)
        {
            if (!known[i])
            {
                break;
            }

            tail++;
        }

        return new PageOutcome
        {
            NewCount = known.Count - matched,
            MatchedCount = matched,
            TailMatched = tail,
            Stop = false,
        };
    }
}
