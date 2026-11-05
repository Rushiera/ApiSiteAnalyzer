using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 面板运行期设置——自动采集开关与间隔。
/// 落在 `data/settings.json`（运行时数据，不入仓）：`config.json` 是**部署期声明**（严格校验、未知键报错），
/// 面板改动属**运行期用户设置**，两者分开，启动链不回写部署配置。
/// </summary>
public sealed class PanelSettings
{
    /// <summary>间隔下限（秒）。</summary>
    public const int MinIntervalSeconds = 3;

    /// <summary>间隔上限（秒）。</summary>
    public const int MaxIntervalSeconds = 119;

    /// <summary>默认间隔（秒）。</summary>
    public const int DefaultIntervalSeconds = 29;

    /// <summary>设置序列化选项。</summary>
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>设置落盘路径（构造时绑定）。</summary>
    private readonly string _path;

    /// <summary>热读闸——Kestrel 请求并发进来，mtime 判定与重读须串行。</summary>
    private readonly object _refreshGate = new object();

    /// <summary>上一次读到的文件修改时刻（UTC）。</summary>
    private DateTime _stampMtime = DateTime.MinValue;

    /// <summary>上一次读到的文件长度——mtime 精度不足时由长度兜底。</summary>
    private long _stampLength = -1;

    /// <summary>构造——绑定落盘路径并读盘（无文件用默认值）。</summary>
    /// <param name="path">设置文件路径（data/settings.json）。</param>
    public PanelSettings(string path)
    {
        _path = path;
        Load();
        CardOrder = NormalizeCardOrder(CardOrder);
        RememberStamp();
    }

    /// <summary>是否开启自动采集（默认开）。</summary>
    public bool AutoEnabled { get; set; } = true;

    /// <summary>自动采集间隔（秒，3–119）——这是**基准**间隔；连续空采时下次间隔按倍数翻倍（上限 30 分钟）。</summary>
    public int AutoIntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>拉取是否走增量（追平即停；默认开）。</summary>
    public bool Incremental { get; set; } = true;

    /// <summary>
    /// 静默采集模式——true = 受控浏览器以 headless 启动（采集全程无窗口），false = 可见窗口。
    /// 默认开：日常采集不需要人看着；登录是低频动作，点「去登录」时才临时切出窗口（登录完自动切回）。
    /// </summary>
    public bool Silent { get; set; } = true;

    /// <summary>
    /// 登录窗口保活——true（默认）：已开着可见窗口时**不再为了静默采集把它停掉**，采集直接复用那个窗口。
    /// 站点会话活在窗口里（贤鱼的 access_token 只在页面内存，靠站点自己的 SPA 续），
    /// 杀掉实例 = 把会话打回要重新换取的状态——保活是「不易掉登录」的关键，
    /// 代价是采集期间会有一个可见窗口。false = 旧行为（采集前停旧起新切 headless）。
    /// </summary>
    public bool KeepLoginWindow { get; set; } = true;

    /// <summary>卡片顺序（拖拽排定的卡片键序列）——与自动采集设置同一份文件，换浏览器 / 清缓存都不丢。</summary>
    public List<string> CardOrder { get; set; } = new List<string>(DefaultCardOrder);

    /// <summary>
    /// 卡片键默认顺序——与面板 `CARD_KEYS` 常量**同值同序**（新增 / 改名 / 改序须同步两端；
    /// 键名与前端常量同为这套字符串，改时先 grep 两端）。
    /// 现行顺序（2026-10-02 Rushiera 排定 · v0.15.3）：
    /// 已探明请求 ID → 本地消费金额 → 近十次平均首字延迟 → 近十次平均耗时 → 输出 Token → 输入 Token →
    /// 缓存命中率 → 近 24 小时已探明请求 ID → 近 24 小时消费金额 → 近十次平均速率 → 缓存 Token →
    /// 近 24 小时输出 Token → 近 24 小时输入 Token → 近 24 小时缓存命中率
    /// </summary>
    public static readonly string[] DefaultCardOrder =
    {
        "requestIds", "amount", "avgFirstToken", "avgUseTime", "completionTokens",
        "promptTokens", "cacheHitRate", "requestIds24h", "amount24h", "avgSpeed",
        "cacheTokens", "completion24h", "prompt24h", "cacheHitRate24h",
    };

    /// <summary>判断卡片键是否已知（未知键一律拒绝——入口面零容忍）。</summary>
    /// <param name="key">卡片键。</param>
    /// <returns>已知返回 true。</returns>
    public static bool IsKnownCardKey(string key)
    {
        foreach (string known in DefaultCardOrder)
        {
            if (string.Equals(known, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 规整卡片顺序——未知键剔除、重复键去重、缺失键按默认顺序补到尾部。
    /// 读盘走这条（旧设置文件没有本字段 → 给全默认顺序；将来新增卡片不会因旧顺序表而丢失）。
    /// </summary>
    /// <param name="order">读到的顺序（可为 null）。</param>
    /// <returns>规整后的完整顺序。</returns>
    public static List<string> NormalizeCardOrder(List<string>? order)
    {
        var result = new List<string>();

        if (order is not null)
        {
            foreach (string key in order)
            {
                if (IsKnownCardKey(key) && !result.Contains(key))
                {
                    result.Add(key);
                }
            }
        }

        foreach (string key in DefaultCardOrder)
        {
            if (!result.Contains(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>设置卡片顺序并落盘——未知键报错拒绝（不静默剔除），缺失键按默认顺序补尾部。</summary>
    /// <param name="raw">逗号分隔的卡片键序列。</param>
    /// <param name="error">失败原因（成功为空串）。</param>
    /// <returns>成功返回 true。</returns>
    public bool SetCardOrder(string raw, out string error)
    {
        error = "";
        var parsed = new List<string>();
        string[] parts = raw.Split(',');

        foreach (string part in parts)
        {
            string key = part.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (!IsKnownCardKey(key))
            {
                error = "未知卡片键：" + key;
                return false;
            }

            parsed.Add(key);
        }

        CardOrder = NormalizeCardOrder(parsed);
        Save();
        return true;
    }

    /// <summary>把间隔夹进合法区间（越界值一律夹紧，不静默留非法值）。</summary>
    /// <param name="seconds">待校验的秒数。</param>
    /// <returns>合法区间内的秒数。</returns>
    public static int ClampInterval(int seconds)
    {
        if (seconds < MinIntervalSeconds)
        {
            return MinIntervalSeconds;
        }

        if (seconds > MaxIntervalSeconds)
        {
            return MaxIntervalSeconds;
        }

        return seconds;
    }

    /// <summary>设置静默采集模式并落盘。</summary>
    /// <param name="silent">是否静默（headless）。</param>
    public void SetSilent(bool silent)
    {
        Silent = silent;
        Save();
    }

    /// <summary>设置登录窗口保活并落盘。</summary>
    /// <param name="keep">是否保活（已开的可见窗口不再为静默停掉）。</param>
    public void SetKeepLoginWindow(bool keep)
    {
        KeepLoginWindow = keep;
        Save();
    }

    /// <summary>更新设置并落盘。</summary>
    /// <param name="autoEnabled">自动采集开关。</param>
    /// <param name="intervalSeconds">间隔（秒）。</param>
    /// <param name="incremental">是否增量拉取。</param>
    public void Update(bool autoEnabled, int intervalSeconds, bool incremental)
    {
        AutoEnabled = autoEnabled;
        AutoIntervalSeconds = ClampInterval(intervalSeconds);
        Incremental = incremental;
        Save();
    }

    /// <summary>读盘（文件不存在 / 损坏 / 读不到一律不动内存值——调用方决定怎么出声）。</summary>
    /// <returns>采纳成功返回 true；文件缺失或内容不可用返回 false（内存值保持原样）。</returns>
    private bool Load()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        try
        {
            string text = File.ReadAllText(_path);
            SettingsFile? loaded = JsonSerializer.Deserialize<SettingsFile>(text, Options);
            if (loaded is null)
            {
                return false;
            }

            AutoEnabled = loaded.AutoEnabled;
            AutoIntervalSeconds = ClampInterval(loaded.AutoIntervalSeconds);
            Incremental = loaded.Incremental;
            /* 旧设置文件没有 silent（DTO 默认 true）→ 静默——升级后默认不再弹窗口 */
            Silent = loaded.Silent;
            /* 旧设置文件没有 keepLoginWindow（DTO 默认 true）→ 保活——升级后不再为静默杀会话 */
            KeepLoginWindow = loaded.KeepLoginWindow;
            /* 旧设置文件没有 cardOrder（读到 null）→ 给全默认顺序——不静默丢卡片 */
            CardOrder = NormalizeCardOrder(loaded.CardOrder);
            return true;
        }
        catch (JsonException)
        {
            // 设置文件损坏——不采纳（下次保存覆盖）
            return false;
        }
        catch (IOException)
        {
            // 读不到——不采纳
            return false;
        }
    }

    /// <summary>
    /// 热读——手改 data/settings.json 后下一次调用即生效（mtime / 长度变化才重读）。
    /// 文件缺失不覆盖内存值；解析失败出声且不采纳（坏文件不把面板打回默认）。
    /// </summary>
    /// <returns>本次采纳了外部改动返回 true；无变化 / 文件缺失 / 内容不可用返回 false。</returns>
    public bool Refresh()
    {
        lock (_refreshGate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return false;
                }

                var info = new FileInfo(_path);
                if (info.LastWriteTimeUtc == _stampMtime && info.Length == _stampLength)
                {
                    return false;
                }

                bool ok = Load();
                if (!ok)
                {
                    Console.Error.WriteLine("settings-reject: 外部改动未采纳（文件损坏或不可读），面板沿用当前设置");
                }

                /* 无论采纳与否都记住这一版的文件戳——坏文件不让每个请求刷一行日志 */
                RememberStamp();
                return ok;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("settings-refresh-fail: " + ex.Message);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine("settings-refresh-fail: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>记录当前文件的时刻与长度（写盘后调用，避免把自己的写入当成外部改动）。</summary>
    private void RememberStamp()
    {
        try
        {
            var info = new FileInfo(_path);
            _stampMtime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            _stampLength = info.Exists ? info.Length : -1;
        }
        catch (IOException)
        {
            _stampMtime = DateTime.MinValue;
            _stampLength = -1;
        }
    }

    /// <summary>落盘（写失败出声——设置不生效必须可见）。</summary>
    private void Save()
    {
        var file = new SettingsFile
        {
            AutoEnabled = AutoEnabled,
            AutoIntervalSeconds = AutoIntervalSeconds,
            Incremental = Incremental,
            Silent = Silent,
            KeepLoginWindow = KeepLoginWindow,
            CardOrder = CardOrder,
        };

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(file, Options));
            RememberStamp();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("设置落盘失败（本次改动只在内存生效）：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("设置落盘失败（本次改动只在内存生效）：" + ex.Message);
        }
    }

    /// <summary>
    /// 落盘结构——**与 PanelSettings 分开**：后者有带参构造（绑路径），
    /// System.Text.Json 会把那个构造当反序列化构造函数，而 path 没有对应属性 → 启动即抛。
    /// 落盘结构用无参 DTO，序列化面与运行态对象解耦。
    /// </summary>
    private sealed class SettingsFile
    {
        /// <summary>是否开启自动采集。</summary>
        public bool AutoEnabled { get; set; } = true;

        /// <summary>自动采集间隔（秒）。</summary>
        public int AutoIntervalSeconds { get; set; } = DefaultIntervalSeconds;

        /// <summary>是否增量拉取。</summary>
        public bool Incremental { get; set; } = true;

        /// <summary>是否静默采集（headless）。</summary>
        public bool Silent { get; set; } = true;

        /// <summary>登录窗口保活（已开的可见窗口不再为静默停掉）。</summary>
        public bool KeepLoginWindow { get; set; } = true;

        /// <summary>卡片顺序（卡片键序列）——缺即 null，由 Load 补全默认顺序。</summary>
        public List<string>? CardOrder { get; set; }
    }
}
