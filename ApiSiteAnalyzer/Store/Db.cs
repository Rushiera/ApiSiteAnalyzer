using System;
using System.Collections.Generic;
using System.Globalization;
using ApiSiteAnalyzer.Sites;
using Microsoft.Data.Sqlite;

namespace ApiSiteAnalyzer.Store;

/// <summary>库行——usage 表的写入单元。</summary>
public sealed class UsageRecord
{
    /// <summary>站点键。</summary>
    public string SiteId { get; set; } = "";

    /// <summary>稳定主键（本站取 request_id——站点返回的 id 是展示序号，会随查询重排）。</summary>
    public string LogKey { get; set; } = "";

    /// <summary>站点展示序号（仅参考，不作键）。</summary>
    public long RemoteId { get; set; }

    /// <summary>发生时刻（Unix 秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>记录类型。</summary>
    public int Type { get; set; }

    /// <summary>模型名。</summary>
    public string ModelName { get; set; } = "";

    /// <summary>令牌名。</summary>
    public string TokenName { get; set; } = "";

    /// <summary>分组名。</summary>
    public string GroupName { get; set; } = "";

    /// <summary>计费额度。</summary>
    public long Quota { get; set; }

    /// <summary>输入 token。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>缓存命中 token。</summary>
    public long CacheTokens { get; set; }

    /// <summary>耗时（秒）。</summary>
    public long UseTime { get; set; }

    /// <summary>首字延迟（毫秒）。</summary>
    public long FirstTokenMs { get; set; }

    /// <summary>输出速率（token/秒）——站点前端口径 = completion_tokens / use_time。</summary>
    public double SpeedTps { get; set; }

    /// <summary>是否流式。</summary>
    public bool IsStream { get; set; }

    /// <summary>渠道 id。</summary>
    public long Channel { get; set; }

    /// <summary>上游模型名。</summary>
    public string UpstreamModel { get; set; } = "";

    /// <summary>请求 id。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>记录正文。</summary>
    public string Content { get; set; } = "";
}

/// <summary>聚合行——分组统计的通用载体（名称 + 计数 + 额度 + token）。</summary>
public sealed class AggregateRow
{
    /// <summary>分组名。</summary>
    public string Name { get; set; } = "";

    /// <summary>条数。</summary>
    public long Count { get; set; }

    /// <summary>额度合计。</summary>
    public long Quota { get; set; }

    /// <summary>输入 token 合计。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token 合计。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>缓存 token 合计。</summary>
    public long CacheTokens { get; set; }
    /// <summary>三种 token 之和（输入 + 输出 + 缓存）——图表的排序与显示口径。</summary>
    public long Token { get; set; }
    /// <summary>已探明请求 ID 数（去重——口径与总览卡片的「已探明请求 ID」一致）。</summary>
    public long RequestIdCount { get; set; }
}

/// <summary>时间窗口统计（近 24 小时 / 前 24 小时）——卡片四项的窗口口径。</summary>
public sealed class WindowStat
{
    /// <summary>窗口内记录条数。</summary>
    public long Count { get; set; }
    /// <summary>窗口内额度合计（原始 quota——金额由 Web 层按该站换算比折算）。</summary>
    public long Quota { get; set; }

    /// <summary>窗口内输入 token 合计。</summary>
    public long PromptTokens { get; set; }

    /// <summary>窗口内输出 token 合计。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>窗口内缓存 token 合计。</summary>
    public long CacheTokens { get; set; }

    /// <summary>窗口内已探明请求 ID 数（去重计数）。</summary>
    public long RequestIdCount { get; set; }
}

/// <summary>总览统计。</summary>
public sealed class OverviewStat
{
    /// <summary>记录条数。</summary>
    public long Count { get; set; }

    /// <summary>额度合计。</summary>
    public long Quota { get; set; }

    /// <summary>输入 token 合计。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token 合计。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>缓存 token 合计。</summary>
    public long CacheTokens { get; set; }

    /// <summary>已探明请求 ID 数（去重计数——库内稳定主键的来源）。</summary>
    public long RequestIdCount { get; set; }

    /// <summary>近十次请求的平均首字延迟（毫秒）。</summary>
    public double RecentAvgFirstTokenMs { get; set; }

    /// <summary>近十次请求的平均耗时（秒）。</summary>
    public double RecentAvgUseTime { get; set; }

    /// <summary>近十次请求的平均输出速率（token/秒）。</summary>
    public double RecentAvgSpeedTps { get; set; }

    /// <summary>近十次窗口内的记录条数（不足十条时即实际条数）。</summary>
    public long RecentSampleCount { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）内的记录条数——卡片对比行的样本数。</summary>
    public long PrevSampleCount { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）的平均首字延迟（毫秒）。</summary>
    public double PrevAvgFirstTokenMs { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）的平均耗时（秒）。</summary>
    public double PrevAvgUseTime { get; set; }

    /// <summary>再往前十次窗口（第 11–20 次）的平均输出速率（token/秒）。</summary>
    public double PrevAvgSpeedTps { get; set; }

    /// <summary>近 24 小时窗口统计（按记录时刻切）。</summary>
    public WindowStat Last24h { get; set; } = new WindowStat();

    /// <summary>前 24 小时窗口统计（24–48 小时前）——四项对比行的参照窗口。</summary>
    public WindowStat Prev24h { get; set; } = new WindowStat();
}

/// <summary>
/// SQLite 库——用量记录的唯一落点。
/// 幂等写入：主键 (site_id, log_key)，重复拉取不产生重复行。
/// **主键不用站点返回的 id**——该字段是展示序号（每次查询从 1 重排），拿它当键会导致整库错位更新。
/// </summary>
public sealed class Db : IDisposable
{
    /// <summary>当前库结构版本（结构变更时递增——不匹配则备份旧库并重建）。</summary>
    private const int SchemaVersion = 2;
    /// <summary>按天图表的补齐窗口（天）——记录跨度超过它时只铺最近这么多天。</summary>
    private const int ByDayWindowDays = 31;

    /// <summary>连接。</summary>
    private readonly SqliteConnection _conn;

    /// <summary>以库文件路径打开（WAL + busy_timeout）。</summary>
    /// <param name="path">库文件路径。</param>
    public Db(string path)
    {
        _conn = new SqliteConnection("Data Source=" + path);
        _conn.Open();

        // [段1] WAL 未生效即抛错——不静默退回 delete 模式
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            object? mode = pragma.ExecuteScalar();
            if (mode is null || !string.Equals(mode.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("WAL 未生效（journal_mode=" + mode + "）");
            }
        }

        using (var busy = _conn.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            busy.ExecuteNonQuery();
        }

        Migrate(path);
        EnsureSchema();
    }

    /// <summary>结构版本核对——不匹配则备份旧库并重建（数据可重拉；备份路径出声）。</summary>
    private void Migrate(string path)
    {
        int current = ReadUserVersion();

        if (current == SchemaVersion)
        {
            return;
        }

        if (current == 0 && !TableExists("usage_log"))
        {
            // 新库——直接建
            WriteUserVersion(SchemaVersion);
            return;
        }

        // [段1] 旧结构——备份后重建（不静默删数据）
        // 连接池会一直持有文件句柄——先清池再关连接，否则 Move 必失败
        _conn.Close();
        SqliteConnection.ClearAllPools();

        string backup = path + ".bak-v" + current + "-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        MoveIfExists(path, backup);
        MoveIfExists(path + "-wal", backup + "-wal");
        MoveIfExists(path + "-shm", backup + "-shm");

        _conn.Open();
        Console.Error.WriteLine("库结构升级（v" + current + " → v" + SchemaVersion + "）：旧库已备份到 " + backup + "，新库需重新拉取。");
        WriteUserVersion(SchemaVersion);
    }

    /// <summary>文件存在则改名（备份用）。</summary>
    private static void MoveIfExists(string from, string to)
    {
        if (System.IO.File.Exists(from))
        {
            System.IO.File.Move(from, to);
        }
    }

    /// <summary>读结构版本。</summary>
    private int ReadUserVersion()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        object? value = cmd.ExecuteScalar();
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>写结构版本。</summary>
    private void WriteUserVersion(int version)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version=" + version + ";";
        cmd.ExecuteNonQuery();
    }

    /// <summary>表是否存在。</summary>
    private bool TableExists(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        object? value = cmd.ExecuteScalar();
        return value is not null && value is not DBNull && Convert.ToInt64(value) > 0;
    }

    /// <summary>建表建索引（幂等）。</summary>
    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS usage_log (
  site_id           TEXT    NOT NULL,
  log_key           TEXT    NOT NULL,
  remote_id         INTEGER NOT NULL DEFAULT 0,
  created_at        INTEGER NOT NULL,
  type              INTEGER NOT NULL,
  model_name        TEXT    NOT NULL DEFAULT '',
  token_name        TEXT    NOT NULL DEFAULT '',
  group_name        TEXT    NOT NULL DEFAULT '',
  quota             INTEGER NOT NULL DEFAULT 0,
  prompt_tokens     INTEGER NOT NULL DEFAULT 0,
  completion_tokens INTEGER NOT NULL DEFAULT 0,
  cache_tokens      INTEGER NOT NULL DEFAULT 0,
  use_time          INTEGER NOT NULL DEFAULT 0,
  first_token_ms    INTEGER NOT NULL DEFAULT 0,
  speed_tps         REAL    NOT NULL DEFAULT 0,
  is_stream         INTEGER NOT NULL DEFAULT 0,
  channel           INTEGER NOT NULL DEFAULT 0,
  upstream_model    TEXT    NOT NULL DEFAULT '',
  request_id        TEXT    NOT NULL DEFAULT '',
  content           TEXT    NOT NULL DEFAULT '',
  PRIMARY KEY (site_id, log_key)
);
CREATE INDEX IF NOT EXISTS idx_usage_site_created ON usage_log (site_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_usage_site_model   ON usage_log (site_id, model_name);
CREATE INDEX IF NOT EXISTS idx_usage_site_type    ON usage_log (site_id, type);

CREATE TABLE IF NOT EXISTS fetch_meta (
  site_id       TEXT PRIMARY KEY,
  last_fetch_at TEXT NOT NULL DEFAULT '',
  total_rows    INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS site_snapshot (
  site_id       TEXT PRIMARY KEY,
  fetched_at    TEXT NOT NULL DEFAULT '',
  total_count   INTEGER NOT NULL DEFAULT 0,
  total_quota   INTEGER NOT NULL DEFAULT 0,
  total_tokens  INTEGER NOT NULL DEFAULT 0,
  avg_rpm       REAL NOT NULL DEFAULT 0,
  avg_tpm       REAL NOT NULL DEFAULT 0
);
";
        cmd.ExecuteNonQuery();
    }

    /// <summary>批量写入（幂等）——同 (site_id, log_key) 覆盖更新。</summary>
    /// <param name="records">记录清单。</param>
    /// <returns>实际写入条数。</returns>
    public int Upsert(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO usage_log (site_id, log_key, remote_id, created_at, type, model_name, token_name, group_name,
  quota, prompt_tokens, completion_tokens, cache_tokens, use_time, first_token_ms, speed_tps, is_stream,
  channel, upstream_model, request_id, content)
VALUES ($site, $key, $rid, $created, $type, $model, $token, $grp,
  $quota, $pt, $ct, $cache, $use, $frt, $tps, $stream,
  $channel, $upstream, $reqid, $content)
ON CONFLICT (site_id, log_key) DO UPDATE SET
  remote_id=excluded.remote_id, created_at=excluded.created_at, type=excluded.type,
  model_name=excluded.model_name, token_name=excluded.token_name, group_name=excluded.group_name,
  quota=excluded.quota, prompt_tokens=excluded.prompt_tokens, completion_tokens=excluded.completion_tokens,
  cache_tokens=excluded.cache_tokens, use_time=excluded.use_time, first_token_ms=excluded.first_token_ms,
  speed_tps=excluded.speed_tps, is_stream=excluded.is_stream, channel=excluded.channel,
  upstream_model=excluded.upstream_model, request_id=excluded.request_id, content=excluded.content;
";

        var pSite = cmd.Parameters.Add("$site", SqliteType.Text);
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pRid = cmd.Parameters.Add("$rid", SqliteType.Integer);
        var pCreated = cmd.Parameters.Add("$created", SqliteType.Integer);
        var pType = cmd.Parameters.Add("$type", SqliteType.Integer);
        var pModel = cmd.Parameters.Add("$model", SqliteType.Text);
        var pToken = cmd.Parameters.Add("$token", SqliteType.Text);
        var pGrp = cmd.Parameters.Add("$grp", SqliteType.Text);
        var pQuota = cmd.Parameters.Add("$quota", SqliteType.Integer);
        var pPt = cmd.Parameters.Add("$pt", SqliteType.Integer);
        var pCt = cmd.Parameters.Add("$ct", SqliteType.Integer);
        var pCache = cmd.Parameters.Add("$cache", SqliteType.Integer);
        var pUse = cmd.Parameters.Add("$use", SqliteType.Integer);
        var pFrt = cmd.Parameters.Add("$frt", SqliteType.Integer);
        var pTps = cmd.Parameters.Add("$tps", SqliteType.Real);
        var pStream = cmd.Parameters.Add("$stream", SqliteType.Integer);
        var pChannel = cmd.Parameters.Add("$channel", SqliteType.Integer);
        var pUpstream = cmd.Parameters.Add("$upstream", SqliteType.Text);
        var pReqId = cmd.Parameters.Add("$reqid", SqliteType.Text);
        var pContent = cmd.Parameters.Add("$content", SqliteType.Text);

        int written = 0;
        foreach (UsageRecord record in records)
        {
            pSite.Value = record.SiteId;
            pKey.Value = record.LogKey;
            pRid.Value = record.RemoteId;
            pCreated.Value = record.CreatedAt;
            pType.Value = record.Type;
            pModel.Value = record.ModelName;
            pToken.Value = record.TokenName;
            pGrp.Value = record.GroupName;
            pQuota.Value = record.Quota;
            pPt.Value = record.PromptTokens;
            pCt.Value = record.CompletionTokens;
            pCache.Value = record.CacheTokens;
            pUse.Value = record.UseTime;
            pFrt.Value = record.FirstTokenMs;
            pTps.Value = record.SpeedTps;
            pStream.Value = record.IsStream ? 1 : 0;
            pChannel.Value = record.Channel;
            pUpstream.Value = record.UpstreamModel;
            pReqId.Value = record.RequestId;
            pContent.Value = record.Content;
            written += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return written;
    }

    /// <summary>记录一次拉取元信息。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="totalRows">库内该站点总行数。</param>
    public void MarkFetched(string siteId, long totalRows)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO fetch_meta (site_id, last_fetch_at, total_rows) VALUES ($site, $at, $rows)
ON CONFLICT (site_id) DO UPDATE SET last_fetch_at=excluded.last_fetch_at, total_rows=excluded.total_rows;";
        cmd.Parameters.AddWithValue("$site", siteId);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$rows", totalRows);
        cmd.ExecuteNonQuery();
    }

    /// <summary>读某站点上次拉取时刻。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>时刻文本（无记录返回空串）。</returns>
    public string LastFetchAt(string siteId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_fetch_at FROM fetch_meta WHERE site_id=$site;";
        cmd.Parameters.AddWithValue("$site", siteId);
        object? value = cmd.ExecuteScalar();
        return value is null || value is DBNull ? "" : value.ToString() ?? "";
    }

    /// <summary>读某站点总行数。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>行数。</returns>
    public long Count(string siteId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_log WHERE site_id=$site;";
        cmd.Parameters.AddWithValue("$site", siteId);
        object? value = cmd.ExecuteScalar();
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>
    /// 读库内最新一条记录的提交时刻（跨站点取最大值）——「工作状态」判定的基准。
    /// 站点日志的 `created_at` 就是请求的提交时刻（unix 秒），故它即「最近一次提交」。
    /// </summary>
    /// <param name="siteIds">站点键清单（空 = 无站点，返回 0）。</param>
    /// <returns>unix 秒；库内无记录返回 0。</returns>
    public long LatestCreatedAt(IReadOnlyList<string> siteIds)
    {
        if (siteIds.Count == 0)
        {
            return 0;
        }

        // [段1] 站点键逐一带参——不拼字符串（键来自配置，但拼接仍是坏习惯）
        using var cmd = _conn.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < siteIds.Count; i++)
        {
            string name = "$s" + i;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, siteIds[i]);
        }

        cmd.CommandText = "SELECT MAX(created_at) FROM usage_log WHERE site_id IN (" + string.Join(",", names) + ");";
        object? value = cmd.ExecuteScalar();
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>写站点口径快照（dashboard/models 页口径——每次实时拉取后覆盖）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="snapshot">快照。</param>
    public void SaveSnapshot(string siteId, SiteSnapshot snapshot)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO site_snapshot (site_id, fetched_at, total_count, total_quota, total_tokens, avg_rpm, avg_tpm)
VALUES ($site, $at, $count, $quota, $tokens, $rpm, $tpm)
ON CONFLICT (site_id) DO UPDATE SET fetched_at=excluded.fetched_at, total_count=excluded.total_count,
  total_quota=excluded.total_quota, total_tokens=excluded.total_tokens,
  avg_rpm=excluded.avg_rpm, avg_tpm=excluded.avg_tpm;";
        cmd.Parameters.AddWithValue("$site", siteId);
        cmd.Parameters.AddWithValue("$at", snapshot.FetchedAt);
        cmd.Parameters.AddWithValue("$count", snapshot.TotalCount);
        cmd.Parameters.AddWithValue("$quota", snapshot.TotalQuota);
        cmd.Parameters.AddWithValue("$tokens", snapshot.TotalTokens);
        cmd.Parameters.AddWithValue("$rpm", snapshot.AvgRpm);
        cmd.Parameters.AddWithValue("$tpm", snapshot.AvgTpm);
        cmd.ExecuteNonQuery();
    }

    /// <summary>读站点口径快照（无记录返回 null）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>快照。</returns>
    public SiteSnapshot? ReadSnapshot(string siteId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT fetched_at, total_count, total_quota, total_tokens, avg_rpm, avg_tpm " +
            "FROM site_snapshot WHERE site_id=$site;";
        cmd.Parameters.AddWithValue("$site", siteId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SiteSnapshot
        {
            FetchedAt = reader.GetString(0),
            TotalCount = reader.GetInt64(1),
            TotalQuota = reader.GetInt64(2),
            TotalTokens = reader.GetInt64(3),
            AvgRpm = reader.GetDouble(4),
            AvgTpm = reader.GetDouble(5),
        };
    }

    /// <summary>总览统计（默认只算消费类记录 type=2）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单（空 = 全部）。</param>
    /// <returns>统计结果。</returns>
    public OverviewStat Overview(string siteId, IReadOnlyList<int> types)
    {
        var stat = new OverviewStat();

        // [段1] 全量合计 + 去重请求 ID 数（请求 ID 是库内稳定主键的来源）
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
        SELECT COUNT(*), COALESCE(SUM(quota),0), COALESCE(SUM(prompt_tokens),0),
               COALESCE(SUM(completion_tokens),0), COALESCE(SUM(cache_tokens),0),
               COUNT(DISTINCT CASE WHEN request_id <> '' THEN request_id END)
        FROM usage_log WHERE site_id=$site" + BuildTypeFilter(cmd, types) + ";";
            cmd.Parameters.AddWithValue("$site", siteId);

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                stat.Count = reader.GetInt64(0);
                stat.Quota = reader.GetInt64(1);
                stat.PromptTokens = reader.GetInt64(2);
                stat.CompletionTokens = reader.GetInt64(3);
                stat.CacheTokens = reader.GetInt64(4);
                stat.RequestIdCount = reader.GetInt64(5);
            }
        }

        // [段2] 两个十次窗口的平均——按时刻倒序（与「最近记录」表同序）取前二十条，row_number 切成两段：
        //       近十次（1–10）与再往前十次（11–20）；两段口径一致（>0 过滤——站点偶给 0 / 负值）
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
        SELECT COALESCE(SUM(CASE WHEN rn <= 10 THEN 1 ELSE 0 END), 0),
               COALESCE(AVG(CASE WHEN rn <= 10 AND first_token_ms > 0 THEN first_token_ms END), 0),
               COALESCE(AVG(CASE WHEN rn <= 10 AND use_time > 0 THEN use_time END), 0),
               COALESCE(AVG(CASE WHEN rn <= 10 AND speed_tps > 0 THEN speed_tps END), 0),
               COALESCE(SUM(CASE WHEN rn > 10 THEN 1 ELSE 0 END), 0),
               COALESCE(AVG(CASE WHEN rn > 10 AND first_token_ms > 0 THEN first_token_ms END), 0),
               COALESCE(AVG(CASE WHEN rn > 10 AND use_time > 0 THEN use_time END), 0),
               COALESCE(AVG(CASE WHEN rn > 10 AND speed_tps > 0 THEN speed_tps END), 0)
        FROM (SELECT first_token_ms, use_time, speed_tps,
                     ROW_NUMBER() OVER (ORDER BY created_at DESC, remote_id DESC) AS rn
              FROM usage_log WHERE site_id=$site" + BuildTypeFilter(cmd, types) + @")
        WHERE rn <= 20;";
            cmd.Parameters.AddWithValue("$site", siteId);

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                stat.RecentSampleCount = reader.GetInt64(0);
                stat.RecentAvgFirstTokenMs = reader.GetDouble(1);
                stat.RecentAvgUseTime = reader.GetDouble(2);
                stat.RecentAvgSpeedTps = reader.GetDouble(3);
                stat.PrevSampleCount = reader.GetInt64(4);
                stat.PrevAvgFirstTokenMs = reader.GetDouble(5);
                stat.PrevAvgUseTime = reader.GetDouble(6);
                stat.PrevAvgSpeedTps = reader.GetDouble(7);
            }
        }

        // [段3] 24 小时窗口——近 24 小时与前 24 小时（两窗相邻不重叠；边界取同一时刻，避免两次取时钟导致窗口错位）
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        stat.Last24h = WindowQuery(siteId, types, now - 86400, now);
        stat.Prev24h = WindowQuery(siteId, types, now - 172800, now - 86400);

        return stat;
    }
    /// <summary>按记录时刻的窗口统计（[from, to) 半开区间）——条数 / 额度 / 三种 token / 去重请求 ID。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单（空 = 全部）。</param>
    /// <param name="from">窗口起点（Unix 秒，含）。</param>
    /// <param name="to">窗口终点（Unix 秒，不含）。</param>
    /// <returns>窗口统计。</returns>
    private WindowStat WindowQuery(string siteId, IReadOnlyList<int> types, long from, long to)
    {
        var stat = new WindowStat();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*), COALESCE(SUM(quota),0), COALESCE(SUM(prompt_tokens),0),
                   COALESCE(SUM(completion_tokens),0),
                   COALESCE(SUM(cache_tokens),0),
                   COUNT(DISTINCT CASE WHEN request_id <> '' THEN request_id END)
            FROM usage_log WHERE site_id=$site AND created_at >= $from AND created_at < $to" + BuildTypeFilter(cmd, types) + ";";
        cmd.Parameters.AddWithValue("$site", siteId);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            stat.Count = reader.GetInt64(0);
            stat.Quota = reader.GetInt64(1);
            stat.PromptTokens = reader.GetInt64(2);
            stat.CompletionTokens = reader.GetInt64(3);
            stat.CacheTokens = reader.GetInt64(4);
            stat.RequestIdCount = reader.GetInt64(5);
        }

        return stat;
    }
    /// <summary>库内已探明请求 ID 总数（去重——口径与总览卡片的「已探明请求 ID」一致）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单（空 = 全部）。</param>
    /// <returns>去重后的请求 ID 数。</returns>
    public long RequestIdTotal(string siteId, IReadOnlyList<int> types)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT request_id) FROM usage_log " +
            "WHERE site_id=$site AND request_id <> ''" + BuildTypeFilter(cmd, types) + ";";
        cmd.Parameters.AddWithValue("$site", siteId);

        object? value = cmd.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt64(value);
    }
    /// <summary>
    /// 按时刻倒序分页取已探明请求 ID 对应的完整记录（去重——口径与总览卡片的「已探明请求 ID」一致）。
    /// 排序带次级键 request_id，保证分页稳定（仅按时刻排序时同刻行次序不定，翻页会重复 / 漏行）。
    /// 同一 request_id 只留最新一条（库内主键 (site_id, log_key) 已保证唯一，此处按 request_id 防御式去重）。
    /// </summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单（空 = 全部）。</param>
    /// <param name="limit">本页条数。</param>
    /// <param name="offset">起始偏移。</param>
    /// <returns>记录清单（完整字段，按时刻倒序）。</returns>
    public List<UsageRecord> RequestIds(string siteId, IReadOnlyList<int> types, int limit, int offset)
    {
        var rows = new List<UsageRecord>();

        using var cmd = _conn.CreateCommand();
        string filter = BuildTypeFilter(cmd, types);
        cmd.CommandText = "SELECT remote_id, created_at, type, model_name, token_name, group_name, quota, " +
            "prompt_tokens, completion_tokens, cache_tokens, use_time, first_token_ms, speed_tps, is_stream, channel, " +
            "upstream_model, request_id, content FROM (" +
            "SELECT *, ROW_NUMBER() OVER (PARTITION BY request_id ORDER BY created_at DESC, log_key ASC) AS rn " +
            "FROM usage_log WHERE site_id=$site AND request_id <> ''" + filter + ") " +
            "WHERE rn = 1 ORDER BY created_at DESC, request_id ASC LIMIT " + limit + " OFFSET " + offset + ";";
        cmd.Parameters.AddWithValue("$site", siteId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new UsageRecord
            {
                SiteId = siteId,
                RemoteId = reader.GetInt64(0),
                CreatedAt = reader.GetInt64(1),
                Type = reader.GetInt32(2),
                ModelName = reader.GetString(3),
                TokenName = reader.GetString(4),
                GroupName = reader.GetString(5),
                Quota = reader.GetInt64(6),
                PromptTokens = reader.GetInt64(7),
                CompletionTokens = reader.GetInt64(8),
                CacheTokens = reader.GetInt64(9),
                UseTime = reader.GetInt64(10),
                FirstTokenMs = reader.GetInt64(11),
                SpeedTps = reader.GetDouble(12),
                IsStream = reader.GetInt64(13) != 0,
                Channel = reader.GetInt64(14),
                UpstreamModel = reader.GetString(15),
                RequestId = reader.GetString(16),
                Content = reader.GetString(17),
            });
        }

        return rows;
    }

    /// <summary>按模型聚合。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <param name="limit">返回条数上限。</param>
    /// <returns>聚合行（按 token 之和倒序）。</returns>
    public List<AggregateRow> ByModel(string siteId, IReadOnlyList<int> types, int limit)
    {
        return GroupBy(siteId, "model_name", types, limit);
    }

    /// <summary>按令牌聚合。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <param name="limit">返回条数上限。</param>
    /// <returns>聚合行（按 token 之和倒序）。</returns>
    public List<AggregateRow> ByToken(string siteId, IReadOnlyList<int> types, int limit)
    {
        return GroupBy(siteId, "token_name", types, limit);
    }

    /// <summary>按天聚合（本地时区）。**空档补齐**——从最早记录日逐日铺到「今天 / 最晚记录日」，无记录日给 0 行；跨度超过窗口（31 天）时只铺最近这么多天。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <returns>聚合行（按日期倒序，Name = yyyy-MM-dd）。</returns>
    public List<AggregateRow> ByDay(string siteId, IReadOnlyList<int> types)
    {
        List<AggregateRow> rows = GroupByExpr(siteId, "strftime('%Y-%m-%d', created_at, 'unixepoch', 'localtime')", types, 0, true);
        if (rows.Count == 0)
        {
            return rows;
        }

        // [段1] 补齐区间——终点取「今天」与「最晚记录日」的较晚者；起点不早于窗口下界
        DateTime today = DateTime.Today;
        DateTime first = DateTime.ParseExact(rows[0].Name, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime last = DateTime.ParseExact(rows[rows.Count - 1].Name, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime end = today;
        if (last > end)
        {
            end = last;
        }

        DateTime start = end.AddDays(-(ByDayWindowDays - 1));
        if (first > start)
        {
            start = first;
        }

        // [段2] 已有行建索引——逐日铺满，无记录日补 0 行（空档补齐）
        Dictionary<string, AggregateRow> known = new Dictionary<string, AggregateRow>(StringComparer.Ordinal);
        foreach (AggregateRow row in rows)
        {
            known[row.Name] = row;
        }

        List<AggregateRow> filled = new List<AggregateRow>();
        for (DateTime day = start; day <= end; day = day.AddDays(1))
        {
            string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            AggregateRow? hit = null;
            if (known.TryGetValue(key, out hit))
            {
                filled.Add(hit);
            }
            else
            {
                filled.Add(new AggregateRow { Name = key });
            }
        }

        // [段3] 顺序反转——铺补按日期升序生成，面板列表要新日期在上
        filled.Reverse();
        return filled;
    }

    /// <summary>按小时聚合（本地时区）。**空档补齐**——固定 00–23 全 24 行，无记录小时给 0 行。不给日期即「当前」滚动口径（今天已过的时刻取今天、今天还没到的时刻取昨天）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <param name="day">限定日期（yyyy-MM-dd，本地时区；空 = 「当前」滚动口径）。</param>
    /// <returns>聚合行（按小时升序，Name = 00..23；「当前」口径下近两天都无记录时为空表）。</returns>
    public List<AggregateRow> ByHour(string siteId, IReadOnlyList<int> types, string day = "")
    {
        /* 指定日期——单日 00–23 聚合；「当前」口径——滚动 24 小时（今天已过的时刻取今天、未到的时刻取昨天） */
        if (day.Length == 0)
        {
            return RollingByHour(siteId, types);
        }

        List<AggregateRow> rows = GroupByExpr(siteId, "strftime('%H', created_at, 'unixepoch', 'localtime')", types, 0, true, day);
        return FillHours(rows);
    }
    /// <summary>
    /// 「当前」口径的小时聚合——**滚动 24 小时**：小时 h ≤ 当前小时取**今天**的记录，h &gt; 当前小时取**昨天**的记录。
    /// 例：14 点时 00–14 时是今天的量、15–23 时是昨天的量。
    /// 不跨天累计——把历史全部叠进同一小时会把「当前时段」的量放大成历史总和。
    /// 近两天都无记录时返回空表（面板显示「暂无数据」，比 24 个空行清楚）。
    /// </summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <returns>聚合行（按小时升序，Name = 00..23；近两天无记录时为空表）。</returns>
    private List<AggregateRow> RollingByHour(string siteId, IReadOnlyList<int> types)
    {
        DateTime now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string yesterday = now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // [段0] 只扫近两天——窗口下界即本地昨天 00:00（created_at 是 Unix 秒）
        long from = new DateTimeOffset(now.Date.AddDays(-1)).ToUnixTimeSeconds();

        // [段1] 一次查出「日期 小时」二维分组的全部近两天记录
        var byDayHour = new Dictionary<string, AggregateRow>(StringComparer.Ordinal);
        using (var cmd = _conn.CreateCommand())
        {
            string filter = BuildTypeFilter(cmd, types);
            cmd.CommandText = "SELECT strftime('%Y-%m-%d %H', created_at, 'unixepoch', 'localtime') AS k, COUNT(*), COALESCE(SUM(quota),0), " +
                "COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(completion_tokens),0), COALESCE(SUM(cache_tokens),0), " +
                "COALESCE(SUM(prompt_tokens),0) + COALESCE(SUM(completion_tokens),0) + COALESCE(SUM(cache_tokens),0), " +
                "COUNT(DISTINCT CASE WHEN request_id <> '' THEN request_id END) " +
                "FROM usage_log WHERE site_id=$site AND created_at >= $from" + filter + " GROUP BY k;";
            cmd.Parameters.AddWithValue("$site", siteId);
            cmd.Parameters.AddWithValue("$from", from);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                byDayHour[reader.GetString(0)] = new AggregateRow
                {
                    Count = reader.GetInt64(1),
                    Quota = reader.GetInt64(2),
                    PromptTokens = reader.GetInt64(3),
                    CompletionTokens = reader.GetInt64(4),
                    CacheTokens = reader.GetInt64(5),
                    Token = reader.GetInt64(6),
                    RequestIdCount = reader.GetInt64(7),
                };
            }
        }

        if (byDayHour.Count == 0)
        {
            return new List<AggregateRow>();
        }

        // [段2] 逐小时取数——已过的时刻认今天、未到的时刻认昨天；两天都没这一小时即铺 0 行（面板淡出）
        var filled = new List<AggregateRow>();
        for (int hour = 0; hour < 24; hour = hour + 1)
        {
            string key = hour.ToString("00", CultureInfo.InvariantCulture);
            string source = (hour <= now.Hour ? today : yesterday) + " " + key;
            AggregateRow? hit = null;
            if (byDayHour.TryGetValue(source, out hit))
            {
                hit.Name = key;
                filled.Add(hit);
            }
            else
            {
                filled.Add(new AggregateRow { Name = key });
            }
        }

        return filled;
    }
    /// <summary>把「小时 → 聚合行」的查询结果铺成 00–23 全 24 行（无记录小时补 0 行——横轴连续，不跳档）。</summary>
    /// <param name="rows">按小时聚合的行（Name = 两位小时）。</param>
    /// <returns>铺满 24 行（按小时升序）。</returns>
    private static List<AggregateRow> FillHours(List<AggregateRow> rows)
    {
        var known = new Dictionary<string, AggregateRow>(StringComparer.Ordinal);
        foreach (AggregateRow row in rows)
        {
            known[row.Name] = row;
        }

        var filled = new List<AggregateRow>();
        for (int hour = 0; hour < 24; hour = hour + 1)
        {
            string key = hour.ToString("00", CultureInfo.InvariantCulture);
            AggregateRow? hit = null;
            if (known.TryGetValue(key, out hit))
            {
                filled.Add(hit);
            }
            else
            {
                filled.Add(new AggregateRow { Name = key });
            }
        }

        return filled;
    }
    /// <summary>把一组聚合行合并成一行（各字段相加）——按天列表顶部「当前」行的合计用它（= 该视图 24 个小时行之和）。</summary>
    /// <param name="rows">聚合行。</param>
    /// <param name="name">合并行的名称。</param>
    /// <returns>合并行（空序列给全 0 行）。</returns>
    public static AggregateRow SumRows(IReadOnlyList<AggregateRow> rows, string name)
    {
        var total = new AggregateRow { Name = name };
        foreach (AggregateRow row in rows)
        {
            total.Count += row.Count;
            total.Quota += row.Quota;
            total.PromptTokens += row.PromptTokens;
            total.CompletionTokens += row.CompletionTokens;
            total.CacheTokens += row.CacheTokens;
            total.Token += row.Token;
            total.RequestIdCount += row.RequestIdCount;
        }

        return total;
    }

    /// <summary>按分组聚合。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <param name="limit">返回条数上限。</param>
    /// <returns>聚合行（按 token 之和倒序）。</returns>
    public List<AggregateRow> ByGroup(string siteId, IReadOnlyList<int> types, int limit)
    {
        return GroupBy(siteId, "group_name", types, limit);
    }

    /// <summary>
    /// 批量比对库内既有行——增量拉取用：站点列表「新的在前」，连续读到与库内完全一致的记录即说明已追平历史。
    /// 一次查询比对整页（不逐条往返）。
    /// </summary>
    /// <param name="records">待比对记录（一次一页）。</param>
    /// <returns>逐条标记（true = 库内已有且字段完全一致）。</returns>
    public List<bool> MatchExisting(IReadOnlyList<UsageRecord> records)
    {
        var flags = new List<bool>();
        if (records.Count == 0)
        {
            return flags;
        }

        // [段1] 预取本批全部主键的库内行
        var existing = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        using (var cmd = _conn.CreateCommand())
        {
            var names = new List<string>();
            for (int i = 0; i < records.Count; i++)
            {
                string name = "$k" + i;
                names.Add(name);
                cmd.Parameters.AddWithValue(name, records[i].LogKey);
            }

            cmd.CommandText = "SELECT site_id, log_key, remote_id, created_at, type, model_name, token_name, group_name, " +
                "quota, prompt_tokens, completion_tokens, cache_tokens, use_time, first_token_ms, speed_tps, is_stream, " +
                "channel, upstream_model, request_id, content FROM usage_log " +
                "WHERE site_id=$site AND log_key IN (" + string.Join(",", names) + ");";
            cmd.Parameters.AddWithValue("$site", records[0].SiteId);

            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    UsageRecord row = ReadRecord(reader);
                    existing[row.LogKey] = row;
                }
            }
        }

        // [段2] 逐条判定——库内没有、或字段有差异，都算「新」
        foreach (UsageRecord record in records)
        {
            UsageRecord? known = null;
            bool hit = existing.TryGetValue(record.LogKey, out known);
            flags.Add(hit && known is not null && SameRow(record, known));
        }

        return flags;
    }

    /// <summary>按 MatchExisting 的查询列序读一行。</summary>
    /// <param name="reader">已定位到数据行的读取器。</param>
    /// <returns>库行。</returns>
    private static UsageRecord ReadRecord(SqliteDataReader reader)
    {
        return new UsageRecord
        {
            SiteId = reader.GetString(0),
            LogKey = reader.GetString(1),
            RemoteId = reader.GetInt64(2),
            CreatedAt = reader.GetInt64(3),
            Type = reader.GetInt32(4),
            ModelName = reader.GetString(5),
            TokenName = reader.GetString(6),
            GroupName = reader.GetString(7),
            Quota = reader.GetInt64(8),
            PromptTokens = reader.GetInt64(9),
            CompletionTokens = reader.GetInt64(10),
            CacheTokens = reader.GetInt64(11),
            UseTime = reader.GetInt64(12),
            FirstTokenMs = reader.GetInt64(13),
            SpeedTps = reader.GetDouble(14),
            IsStream = reader.GetInt64(15) != 0,
            Channel = reader.GetInt64(16),
            UpstreamModel = reader.GetString(17),
            RequestId = reader.GetString(18),
            Content = reader.GetString(19),
        };
    }

    /// <summary>
    /// 比对两条记录的业务字段是否完全一致。
    /// **不比 remote_id**——站点展示序号每次查询从 1 重排，不承载语义（拿它判定会误报「有变化」）。
    /// </summary>
    /// <param name="left">新拉取记录。</param>
    /// <param name="right">库内既有行。</param>
    /// <returns>是否一致。</returns>
    public static bool SameRow(UsageRecord left, UsageRecord right)
    {
        if (!string.Equals(left.SiteId, right.SiteId, StringComparison.Ordinal) ||
            !string.Equals(left.LogKey, right.LogKey, StringComparison.Ordinal) ||
            left.CreatedAt != right.CreatedAt ||
            left.Type != right.Type ||
            !string.Equals(left.ModelName, right.ModelName, StringComparison.Ordinal) ||
            !string.Equals(left.TokenName, right.TokenName, StringComparison.Ordinal) ||
            !string.Equals(left.GroupName, right.GroupName, StringComparison.Ordinal) ||
            left.Quota != right.Quota ||
            left.PromptTokens != right.PromptTokens ||
            left.CompletionTokens != right.CompletionTokens ||
            left.CacheTokens != right.CacheTokens ||
            left.UseTime != right.UseTime ||
            left.FirstTokenMs != right.FirstTokenMs ||
            left.SpeedTps != right.SpeedTps ||
            left.IsStream != right.IsStream ||
            left.Channel != right.Channel ||
            !string.Equals(left.UpstreamModel, right.UpstreamModel, StringComparison.Ordinal) ||
            !string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal) ||
            !string.Equals(left.Content, right.Content, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>按列名聚合。</summary>
    private List<AggregateRow> GroupBy(string siteId, string column, IReadOnlyList<int> types, int limit)
    {
        return GroupByExpr(siteId, column, types, limit, false);
    }

    /// <summary>按任意表达式聚合。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="expression">分组表达式（SQL 片段）。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <param name="limit">返回条数上限（0 = 不限）。</param>
    /// <param name="ascending">true = 按分组名升序，false = 按 token 之和倒序。</param>
    /// <param name="whereDay">限定日期（yyyy-MM-dd，本地时区；空 = 不限定）。</param>
    private List<AggregateRow> GroupByExpr(string siteId, string expression, IReadOnlyList<int> types, int limit, bool ascending, string whereDay = "")
    {
        var rows = new List<AggregateRow>();

        using var cmd = _conn.CreateCommand();
        string order = ascending ? "ORDER BY 1 ASC" : "ORDER BY 7 DESC";
        string tail = limit > 0 ? " LIMIT " + limit : "";
        string filter = BuildTypeFilter(cmd, types);
        if (whereDay.Length > 0)
        {
            filter = filter + " AND strftime('%Y-%m-%d', created_at, 'unixepoch', 'localtime') = $day";
            cmd.Parameters.AddWithValue("$day", whereDay);
        }

        cmd.CommandText = "SELECT " + expression + " AS k, COUNT(*), COALESCE(SUM(quota),0), " +
            "COALESCE(SUM(prompt_tokens),0), COALESCE(SUM(completion_tokens),0), COALESCE(SUM(cache_tokens),0), " +
            "COALESCE(SUM(prompt_tokens),0) + COALESCE(SUM(completion_tokens),0) + COALESCE(SUM(cache_tokens),0), " +
            "COUNT(DISTINCT CASE WHEN request_id <> '' THEN request_id END) " +
            "FROM usage_log WHERE site_id=$site" + filter + " GROUP BY k " + order + tail + ";";
        cmd.Parameters.AddWithValue("$site", siteId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AggregateRow
            {
                Name = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Count = reader.GetInt64(1),
                Quota = reader.GetInt64(2),
                PromptTokens = reader.GetInt64(3),
                CompletionTokens = reader.GetInt64(4),
                CacheTokens = reader.GetInt64(5),
                Token = reader.GetInt64(6),
                RequestIdCount = reader.GetInt64(7),
            });
        }

        return rows;
    }

    /// <summary>最近记录（按时间倒序）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="types">记录类型白名单。</param>
    /// <param name="limit">返回条数。</param>
    /// <returns>记录清单。</returns>
    public List<UsageRecord> Recent(string siteId, IReadOnlyList<int> types, int limit)
    {
        var rows = new List<UsageRecord>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT remote_id, created_at, type, model_name, token_name, group_name, quota, " +
            "prompt_tokens, completion_tokens, cache_tokens, use_time, first_token_ms, speed_tps, is_stream, channel, " +
            "upstream_model, request_id, content FROM usage_log WHERE site_id=$site" +
            BuildTypeFilter(cmd, types) + " ORDER BY created_at DESC, remote_id DESC LIMIT " + limit + ";";
        cmd.Parameters.AddWithValue("$site", siteId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new UsageRecord
            {
                SiteId = siteId,
                RemoteId = reader.GetInt64(0),
                CreatedAt = reader.GetInt64(1),
                Type = reader.GetInt32(2),
                ModelName = reader.GetString(3),
                TokenName = reader.GetString(4),
                GroupName = reader.GetString(5),
                Quota = reader.GetInt64(6),
                PromptTokens = reader.GetInt64(7),
                CompletionTokens = reader.GetInt64(8),
                CacheTokens = reader.GetInt64(9),
                UseTime = reader.GetInt64(10),
                FirstTokenMs = reader.GetInt64(11),
                SpeedTps = reader.GetDouble(12),
                IsStream = reader.GetInt64(13) != 0,
                Channel = reader.GetInt64(14),
                UpstreamModel = reader.GetString(15),
                RequestId = reader.GetString(16),
                Content = reader.GetString(17),
            });
        }

        return rows;
    }

    /// <summary>拼类型过滤片段（同时登记参数）。</summary>
    private static string BuildTypeFilter(SqliteCommand cmd, IReadOnlyList<int> types)
    {
        if (types.Count == 0)
        {
            return "";
        }

        var names = new List<string>();
        for (int i = 0; i < types.Count; i++)
        {
            string name = "$t" + i;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, types[i]);
        }

        return " AND type IN (" + string.Join(",", names) + ")";
    }

    /// <summary>关闭连接。</summary>
    public void Dispose()
    {
        _conn.Dispose();
    }
}
