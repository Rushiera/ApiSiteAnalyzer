using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ApiSiteAnalyzer.Config;

/// <summary>
/// API key 存储——落 `data/keys.json`（**运行期数据，不入仓**）。
/// </summary>
/// <remarks>
/// **特批**（2026-10-01 · Rushiera 单笔授权 · 仅此一次 · 范围仅限 47.250.160.225:8443 一站）。
/// 理由——该网站**没有用户名与密码设置**（不存在账号登录体系）；**API key 不二次提供**
/// （签发后只显示一次，丢了无法重新取回）；网站全部功能仅认这一个唯一 id（即 API key）。
/// 故没有「登录归人」的路径可走，key 必须由本工具持有明文。
/// 边界——不写进 `config.json`（该文件随 git 推公开仓，落进去等于公开凭据），只落本文件；
/// 本存储不引入任何其他站点（其余站点仍守「凭据不出浏览器」）。
/// </remarks>
public sealed class KeyStore
{
    /// <summary>落盘文件路径。</summary>
    private readonly string _path;

    /// <summary>站点键 → API key。</summary>
    private readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>以文件路径打开（文件不存在按空表处理——首次运行是正常态）。</summary>
    /// <param name="path">keys.json 路径。</param>
    public KeyStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>读某站点的 key（没有返回空串）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>API key。</returns>
    public string Get(string siteId)
    {
        return _keys.TryGetValue(siteId, out string? key) ? key : "";
    }

    /// <summary>写某站点的 key 并立即落盘。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="key">API key。</param>
    public void Set(string siteId, string key)
    {
        _keys[siteId] = key;
        Save();
    }

    /// <summary>读盘——缺文件 / 空文件按空表；结构异常出声（不静默当成没有）。</summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        string raw = File.ReadAllText(_path);
        if (raw.Trim().Length == 0)
        {
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("keys.json 结构异常（顶层必须是对象）：" + _path);
        }

        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                _keys[property.Name] = property.Value.GetString() ?? "";
            }
        }
    }

    /// <summary>落盘（缩进可读；key 明文——文件位于 .gitignore 覆盖的 data/ 下）。</summary>
    private void Save()
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_path, JsonSerializer.Serialize(_keys, options));
    }
}
