using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ApiSiteAnalyzer.Config;

/// <summary>配置错误——未知键 / 缺键 / 类型错一律抛此异常，不静默回落。</summary>
public sealed class ConfigException : Exception
{
    /// <summary>以说明文本构造。</summary>
    /// <param name="message">说明文本。</param>
    public ConfigException(string message)
        : base(message)
    {
    }
}

/// <summary>单个站点配置。</summary>
public sealed class SiteConfig
{
    /// <summary>站点键（库内唯一）。</summary>
    public string Id { get; set; } = "";

    /// <summary>适配器类型（当前仅 newapi）。</summary>
    public string Type { get; set; } = "newapi";

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>站点根地址。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>浏览器用户目录（登录态载体；空 = 用 data/profiles/&lt;id&gt;）。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>每货币单位 quota 数（New API 默认 500000）。</summary>
    public double QuotaPerUnit { get; set; } = 500000;

    /// <summary>货币符号。</summary>
    public string CurrencySymbol { get; set; } = "¥";
}

/// <summary>全量配置。</summary>
public sealed class AppConfig
{
    /// <summary>chrome.exe 路径（空 = 自动探测）。</summary>
    public string ChromePath { get; set; } = "";

    /// <summary>面板端口。</summary>
    public int Port { get; set; } = 8766;

    /// <summary>站点清单。</summary>
    public List<SiteConfig> Sites { get; set; } = new List<SiteConfig>();
}

/// <summary>配置加载器——严格模式：未知键报错、缺键用默认、类型错报错。</summary>
public static class ConfigLoader
{
    /// <summary>加载配置文件。</summary>
    /// <param name="path">配置文件路径。</param>
    /// <returns>配置对象。</returns>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("配置文件不存在：" + path);
        }

        string text = File.ReadAllText(path);
        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ConfigException("配置文件不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException("配置文件根节点必须是对象");
            }

            var config = new AppConfig();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chromePath", "port", "sites" };

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!known.Contains(property.Name))
                {
                    throw new ConfigException("未知配置键：" + property.Name);
                }
            }

            config.ChromePath = ReadString(root, "chromePath", "");
            config.Port = ReadInt(root, "port", 8766);

            if (!root.TryGetProperty("sites", out JsonElement sites) || sites.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("配置缺 sites 数组");
            }

            foreach (JsonElement item in sites.EnumerateArray())
            {
                config.Sites.Add(ReadSite(item));
            }

            if (config.Sites.Count == 0)
            {
                throw new ConfigException("sites 为空——至少配一个站点");
            }

            ValidateUnique(config);
            return config;
        }
    }

    /// <summary>读一个站点条目。</summary>
    private static SiteConfig ReadSite(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException("sites 条目必须是对象");
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "type", "displayName", "baseUrl", "profileDir", "quotaPerUnit", "currencySymbol",
        };

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                throw new ConfigException("站点条目含未知键：" + property.Name);
            }
        }

        var site = new SiteConfig
        {
            Id = ReadString(element, "id", ""),
            Type = ReadString(element, "type", "newapi"),
            DisplayName = ReadString(element, "displayName", ""),
            BaseUrl = ReadString(element, "baseUrl", ""),
            ProfileDir = ReadString(element, "profileDir", ""),
            QuotaPerUnit = ReadDouble(element, "quotaPerUnit", 500000),
            CurrencySymbol = ReadString(element, "currencySymbol", "¥"),
        };

        if (site.Id.Length == 0)
        {
            throw new ConfigException("站点缺 id");
        }

        if (site.BaseUrl.Length == 0)
        {
            throw new ConfigException("站点 " + site.Id + " 缺 baseUrl");
        }

        if (!string.Equals(site.Type, "newapi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException("站点 " + site.Id + " 的 type 非法（当前仅支持 newapi）：" + site.Type);
        }

        if (site.DisplayName.Length == 0)
        {
            site.DisplayName = site.Id;
        }

        return site;
    }

    /// <summary>站点键查重。</summary>
    private static void ValidateUnique(AppConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SiteConfig site in config.Sites)
        {
            if (!seen.Add(site.Id))
            {
                throw new ConfigException("站点 id 重复：" + site.Id);
            }
        }
    }

    /// <summary>读字符串键（缺 = 默认值）。</summary>
    private static string ReadString(JsonElement parent, string name, string fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ConfigException("配置键 " + name + " 必须是字符串");
        }

        return value.GetString() ?? fallback;
    }

    /// <summary>读整数键（缺 = 默认值）。</summary>
    private static int ReadInt(JsonElement parent, string name, int fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
        {
            throw new ConfigException("配置键 " + name + " 必须是整数");
        }

        return parsed;
    }

    /// <summary>读浮点键（缺 = 默认值）。</summary>
    private static double ReadDouble(JsonElement parent, string name, double fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double parsed))
        {
            throw new ConfigException("配置键 " + name + " 必须是数字");
        }

        return parsed;
    }
}
