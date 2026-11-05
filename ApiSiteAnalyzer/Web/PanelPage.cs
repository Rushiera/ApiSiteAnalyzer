using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 面板单页的**文件化存储**——内嵌资源是出厂件，`data/web/index.html` 是可改件。
/// 改文件后下一次 GET `/` 即按 mtime 重读，不需要重编译、不需要重启进程。
/// 文件缺失 / 读不到 / 用户未改过时一律回退内嵌件，不空页。
/// </summary>
public sealed class PanelPage
{
    /// <summary>内嵌单页资源名（csproj 的 EmbeddedResource 决定）。</summary>
    private const string ResourceName = "ApiSiteAnalyzer.Web.wwwroot.index.html";

    /// <summary>导出件首行的版本标记前缀——升级判定靠它，删除即视为用户自定义件。</summary>
    private const string VersionMarkPrefix = "<!-- panel-page ";

    /// <summary>导出件的落盘路径（构造时绑定）。</summary>
    private readonly string _filePath;

    /// <summary>缓存与导出状态的串行化闸（Kestrel 请求并发进来）。</summary>
    private readonly object _gate = new object();

    /// <summary>上一次读到的内容。</summary>
    private string _cached = "";

    /// <summary>上一次读到的文件修改时刻（UTC）。</summary>
    private DateTime _cachedMtime = DateTime.MinValue;

    /// <summary>上一次读到的文件长度——mtime 精度不足时由长度兜底。</summary>
    private long _cachedLength = -1;

    /// <summary>是否已尝试过自举导出（失败只出声一次）。</summary>
    private bool _exportTried;

    /// <summary>构造——绑定数据目录，并把内嵌件导出成可改件（尽力而为，失败回落内嵌）。</summary>
    /// <param name="dataDir">数据目录（`data/web/index.html`）。</param>
    public PanelPage(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "web", "index.html");
        Read();
    }

    /// <summary>可改件的落盘路径（面板页脚 / 诊断用）。</summary>
    public string FilePath => _filePath;

    /// <summary>当前生效来源：`file` = 可改件，`embedded` = 内嵌件。</summary>
    public string Source
    {
        get
        {
            lock (_gate)
            {
                return File.Exists(_filePath) ? "file" : "embedded";
            }
        }
    }

    /// <summary>
    /// 读取单页——文件存在且可读给文件内容，否则回退内嵌件。
    /// 文件 mtime 或长度变化即重读；读失败出声并回落内嵌（失败必须可见）。
    /// </summary>
    /// <returns>单页 HTML。</returns>
    public string Read()
    {
        lock (_gate)
        {
            EnsureExported();

            if (File.Exists(_filePath))
            {
                try
                {
                    var info = new FileInfo(_filePath);
                    if (info.LastWriteTimeUtc != _cachedMtime || info.Length != _cachedLength)
                    {
                        _cached = File.ReadAllText(_filePath, Encoding.UTF8);
                        _cachedMtime = info.LastWriteTimeUtc;
                        _cachedLength = info.Length;
                    }

                    return _cached;
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine("page-read-fail: " + ex.Message + "（本次回退内嵌单页）");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.Error.WriteLine("page-read-fail: " + ex.Message + "（本次回退内嵌单页）");
                }
            }

            return Embedded();
        }
    }

    /// <summary>内嵌单页的当前版本（程序集版本，导出标记与诊断用）。</summary>
    /// <returns>版本文本（如 `0.24.2`）。</returns>
    public static string EmbeddedVersion()
    {
        Version? version = typeof(PanelPage).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }

    /// <summary>
    /// 自举导出——首次给 data/web 落下可改件；内嵌版本变化时把旧件转 `.bak` 再导出。
    /// 用户把首行版本标记删掉视作自定义件，不覆盖（避免升级吃掉手改内容）。
    /// </summary>
    private void EnsureExported()
    {
        if (_exportTried)
        {
            return;
        }

        _exportTried = true;

        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string version = EmbeddedVersion();
            string mark = VersionMarkPrefix + "v" + version + " -->";

            if (!File.Exists(_filePath))
            {
                File.WriteAllText(_filePath, mark + Environment.NewLine + Embedded(), new UTF8Encoding(false));
                Console.WriteLine("page-export: " + _filePath + "（v" + version + "）");
                return;
            }

            string head = ReadHead(_filePath);
            if (!head.StartsWith(VersionMarkPrefix, StringComparison.Ordinal))
            {
                Console.WriteLine("page-custom: " + _filePath + "（无版本标记，按自定义件保留，不随升级覆盖）");
                return;
            }

            if (head.StartsWith(mark, StringComparison.Ordinal))
            {
                return;
            }

            string backup = _filePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(_filePath, backup);
            File.WriteAllText(_filePath, mark + Environment.NewLine + Embedded(), new UTF8Encoding(false));
            Console.WriteLine("page-upgrade: v" + head.Substring(VersionMarkPrefix.Length).TrimEnd(' ', '-', '>') + " → v" + version + "，旧件转 " + backup);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("page-export-fail: " + ex.Message + "（本次使用内嵌单页）");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("page-export-fail: " + ex.Message + "（本次使用内嵌单页）");
        }
    }

    /// <summary>读文件首行（导出标记所在行）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>首行文本（无内容返回空串）。</returns>
    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        string? line = reader.ReadLine();
        return line ?? "";
    }

    /// <summary>读内嵌单页（出厂件）。</summary>
    /// <returns>单页 HTML；资源缺失时返回可见的错误页而不是空页。</returns>
    private static string Embedded()
    {
        var assembly = typeof(PanelPage).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return "<!doctype html><meta charset=\"utf-8\"><h1>面板资源缺失</h1><p>内嵌单页 " + ResourceName + " 未找到</p>";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
