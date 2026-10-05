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

    /// <summary>构造——绑定落盘路径并读盘（无文件用默认值）。</summary>
    /// <param name="path">设置文件路径（data/settings.json）。</param>
    public PanelSettings(string path)
    {
        _path = path;
        Load();
        CardOrder = NormalizeCardOrder(CardOrder);
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

    /// <summary>读盘（文件不存在 / 损坏 / 读不到一律用默认值——出声由调用方决定）。</summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(_path);
            SettingsFile? loaded = JsonSerializer.Deserialize<SettingsFile>(text, Options);
            if (loaded is null)
            {
                return;
            }

            AutoEnabled = loaded.AutoEnabled;
            AutoIntervalSeconds = ClampInterval(loaded.AutoIntervalSeconds);
            Incremental = loaded.Incremental;
            /* 旧设置文件没有 silent（DTO 默认 true）→ 静默——升级后默认不再弹窗口 */
            Silent = loaded.Silent;
            /* 旧设置文件没有 cardOrder（读到 null）→ 给全默认顺序——不静默丢卡片 */
            CardOrder = NormalizeCardOrder(loaded.CardOrder);
        }
        catch (JsonException)
        {
            // 设置文件损坏——按默认值处理（下次保存覆盖）
        }
        catch (IOException)
        {
            // 读不到——按默认值处理
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

        /// <summary>卡片顺序（卡片键序列）——缺即 null，由 Load 补全默认顺序。</summary>
        public List<string>? CardOrder { get; set; }
    }
}
