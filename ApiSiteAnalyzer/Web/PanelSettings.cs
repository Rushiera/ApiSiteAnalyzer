using System;
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
    }

    /// <summary>是否开启自动采集（默认开）。</summary>
    public bool AutoEnabled { get; set; } = true;

    /// <summary>自动采集间隔（秒，3–119）——这是**基准**间隔；连续空采时下次间隔按倍数翻倍（上限 30 分钟）。</summary>
    public int AutoIntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>拉取是否走增量（追平即停；默认开）。</summary>
    public bool Incremental { get; set; } = true;

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
    }
}
