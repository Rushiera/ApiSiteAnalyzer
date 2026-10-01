using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ApiSiteAnalyzer.Browser;

/// <summary>受控实例信息。</summary>
public sealed class BrowserInstance
{
    /// <summary>浏览器用户目录。</summary>
    public string ProfileDir { get; set; } = "";

    /// <summary>调试端口。</summary>
    public int Port { get; set; }

    /// <summary>进程 id。</summary>
    public int Pid { get; set; }

    /// <summary>启动时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>通道标签页 id（采集在它里面执行，不影响用户正在看的页面）。</summary>
    public string ChannelTargetId { get; set; } = "";

    /// <summary>
    /// 已注入钩子的站点键（逗号分隔）。
    /// **注入状态必须跨进程持久**——`Page.addScriptToEvaluateOnNewDocument` 的注入列表挂在页面上，
    /// 而通道标签页长期存活；若只记在进程内存里，下次 CLI 启动会重复注入（列表累积）。
    /// </summary>
    public string HookedSites { get; set; } = "";

    /// <summary>判断某站点是否已注入过钩子。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>是否已注入。</returns>
    public bool HasHook(string siteId)
    {
        foreach (string part in HookedSites.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part.Trim(), siteId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>登记某站点已注入钩子。</summary>
    /// <param name="siteId">站点键。</param>
    public void MarkHook(string siteId)
    {
        if (HasHook(siteId))
        {
            return;
        }

        HookedSites = HookedSites.Length == 0 ? siteId : HookedSites + "," + siteId;
    }
}

/// <summary>登记表落盘结构。</summary>
internal sealed class BrowserRegistry
{
    /// <summary>实例清单。</summary>
    public List<BrowserInstance> Instances { get; set; } = new List<BrowserInstance>();
}

/// <summary>标签页信息。</summary>
public sealed class TargetInfo
{
    /// <summary>标签页 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>页面级调试端点。</summary>
    public string SocketUrl { get; set; } = "";
}

/// <summary>
/// 受控浏览器中心——登记「由本程序启动、保留调试端口的浏览器实例」（用户目录 → 端口 / 进程），
/// 负责复用探活、按需启动、取通道标签页。**凭据不出浏览器**：本类只驱动浏览器，不读不写任何 cookie。
/// </summary>
public sealed class BrowserHub
{
    /// <summary>登记表序列化选项。</summary>
    private static readonly JsonSerializerOptions RegistryOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>调试端点请求用的共享客户端。</summary>
    private static readonly HttpClient Http = new HttpClient();

    /// <summary>chrome.exe 路径。</summary>
    private readonly string _chromePath;

    /// <summary>登记表路径（data/browsers.json）。</summary>
    private readonly string _registryPath;

    /// <summary>登记表读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>受控实例表（用户目录 → 实例）。</summary>
    private readonly Dictionary<string, BrowserInstance> _instances =
        new Dictionary<string, BrowserInstance>(StringComparer.OrdinalIgnoreCase);

    /// <summary>启动互斥——避免同一用户目录并发起两个窗口。</summary>
    private readonly SemaphoreSlim _launchLock = new SemaphoreSlim(1, 1);

    /// <summary>构造受控浏览器中心（启动即读登记表）。</summary>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="registryPath">登记表路径（data/browsers.json）。</param>
    public BrowserHub(string chromePath, string registryPath)
    {
        _chromePath = chromePath;
        _registryPath = registryPath;
        LoadRegistry();
    }

    /// <summary>chrome.exe 路径。</summary>
    public string ChromePath => _chromePath;

    /// <summary>取该用户目录的存活受控实例（不启动）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <returns>存活实例；无则 null。</returns>
    public async Task<BrowserInstance?> FindAliveAsync(string profileDir)
    {
        BrowserInstance? known = Get(profileDir);
        if (known is null)
        {
            return null;
        }

        if (await IsAliveAsync(known.Port, CancellationToken.None).ConfigureAwait(false))
        {
            return known;
        }

        Remove(profileDir);
        return null;
    }

    /// <summary>确保该用户目录有一个受控实例——活着就复用，否则启动可见窗口。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="url">启动时打开的地址（复用已有实例时导航到它）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受控实例 + 本次是否新建。</returns>
    public async Task<(BrowserInstance Instance, bool Launched)> EnsureAsync(string profileDir, string url, CancellationToken ct)
    {
        BrowserInstance? alive = await FindAliveAsync(profileDir).ConfigureAwait(false);
        if (alive is not null)
        {
            return (alive, false);
        }

        await _launchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            alive = await FindAliveAsync(profileDir).ConfigureAwait(false);
            if (alive is not null)
            {
                return (alive, false);
            }

            ChromeProcess chrome = await ChromeLauncher.StartWindowAsync(_chromePath, profileDir, url, 30000, ct).ConfigureAwait(false);
            var instance = new BrowserInstance
            {
                ProfileDir = profileDir,
                Port = chrome.Port,
                Pid = chrome.Pid,
                StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ChannelTargetId = "",
            };

            lock (_gate)
            {
                _instances[profileDir] = instance;
                SaveRegistry();
            }

            return (instance, true);
        }
        finally
        {
            _launchLock.Release();
        }
    }

    /// <summary>取该实例的通道标签页页面通道——不存在则新建一个空白页并登记。</summary>
    /// <param name="instance">受控实例。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>页面级调试端点（ws://…）。</returns>
    public async Task<string> EnsureChannelSocketAsync(BrowserInstance instance, CancellationToken ct)
    {
        // [段1] 登记的通道标签页仍活着 → 直接复用
        if (instance.ChannelTargetId.Length > 0)
        {
            string? found = await FindTargetSocketAsync(instance.Port, instance.ChannelTargetId, ct).ConfigureAwait(false);
            if (found is not null)
            {
                return found;
            }
        }

        // [段2] 新建通道标签页并登记
        TargetInfo? created = await CreateTargetAsync(instance.Port, "about:blank", ct).ConfigureAwait(false);
        if (created is null)
        {
            throw new InvalidOperationException("受控浏览器未接受新标签页请求（端口 " + instance.Port + "）");
        }

        instance.ChannelTargetId = created.Id;
        lock (_gate)
        {
            SaveRegistry();
        }

        return created.SocketUrl;
    }

    /// <summary>在受控实例里打开一个前台标签页（用户点了「打开登录」要看得见）。</summary>
    /// <param name="instance">受控实例。</param>
    /// <param name="url">目标地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task OpenTabAsync(BrowserInstance instance, string url, CancellationToken ct)
    {
        TargetInfo? target = await CreateTargetAsync(instance.Port, url, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new InvalidOperationException("受控浏览器未接受新标签页请求（端口 " + instance.Port + "）");
        }
    }

    /// <summary>持久化登记表（外部改了实例字段后调用）。</summary>
    public void Persist()
    {
        lock (_gate)
        {
            SaveRegistry();
        }
    }

    /// <summary>探测端口上的浏览器是否还在。</summary>
    /// <param name="port">调试端口。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否存活。</returns>
    public static async Task<bool> IsAliveAsync(int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(1500);

        try
        {
            string text = await Http.GetStringAsync("http://127.0.0.1:" + port + "/json/version", cts.Token).ConfigureAwait(false);
            return text.Length > 0;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>列出该用户目录下正在运行的 chrome 主进程（占用判定的权威来源）。</summary>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>进程 id 清单（空表 = 无人占用）。</returns>
    public static async Task<List<int>> FindOccupantsAsync(string profileDir, CancellationToken ct)
    {
        List<(int Pid, string CommandLine)> procs = await QueryChromeMainsAsync(ct).ConfigureAwait(false);
        var result = new List<int>();

        foreach ((int pid, string commandLine) in procs)
        {
            if (string.Equals(ReadProfileDir(commandLine), profileDir, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(pid);
            }
        }

        return result;
    }

    /// <summary>查登记表里的实例（不探活）。</summary>
    private BrowserInstance? Get(string profileDir)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(profileDir, out BrowserInstance? found))
            {
                return found;
            }

            return null;
        }
    }

    /// <summary>从登记表移除一个实例并落盘。</summary>
    private void Remove(string profileDir)
    {
        lock (_gate)
        {
            if (_instances.Remove(profileDir))
            {
                SaveRegistry();
            }
        }
    }

    /// <summary>读登记表。</summary>
    private void LoadRegistry()
    {
        lock (_gate)
        {
            if (!File.Exists(_registryPath))
            {
                return;
            }

            try
            {
                string text = File.ReadAllText(_registryPath);
                BrowserRegistry? registry = JsonSerializer.Deserialize<BrowserRegistry>(text, RegistryOptions);
                if (registry is null)
                {
                    return;
                }

                foreach (BrowserInstance instance in registry.Instances)
                {
                    if (instance.ProfileDir.Length > 0)
                    {
                        _instances[instance.ProfileDir] = instance;
                    }
                }
            }
            catch (JsonException)
            {
                // 登记表损坏——按空表处理（下次写盘覆盖）
            }
            catch (IOException)
            {
                // 读不到——按空表处理
            }
        }
    }

    /// <summary>写登记表（调用方持锁）。</summary>
    private void SaveRegistry()
    {
        var registry = new BrowserRegistry();
        registry.Instances.AddRange(_instances.Values);

        string? dir = Path.GetDirectoryName(_registryPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_registryPath, JsonSerializer.Serialize(registry, RegistryOptions));
    }

    /// <summary>按 target id 找页面级调试端点。</summary>
    private async Task<string?> FindTargetSocketAsync(int port, string targetId, CancellationToken ct)
    {
        string? body = await SendAsync(HttpMethod.Get, "http://127.0.0.1:" + port + "/json/list", ct).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out JsonElement id) || id.GetString() != targetId)
                {
                    continue;
                }

                if (item.TryGetProperty("webSocketDebuggerUrl", out JsonElement socket))
                {
                    string url = socket.GetString() ?? "";
                    return url.Length > 0 ? url : null;
                }
            }
        }
        catch (JsonException)
        {
            // 列表不可解析——按未找到处理。
        }

        return null;
    }

    /// <summary>让浏览器开一个新标签页。</summary>
    private static async Task<TargetInfo?> CreateTargetAsync(int port, string url, CancellationToken ct)
    {
        string path = "http://127.0.0.1:" + port + "/json/new?url=" + Uri.EscapeDataString(url);

        // [段1] 新版 chrome 只认 PUT——失败退回 GET（旧版）
        string? body = await SendAsync(HttpMethod.Put, path, ct).ConfigureAwait(false);
        if (body is null)
        {
            body = await SendAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        }

        if (body is null)
        {
            return null;
        }

        return ParseTarget(body);
    }

    /// <summary>解析新建标签页的返回（对象或数组）。</summary>
    private static TargetInfo? ParseTarget(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return null;
                }

                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string id = root.TryGetProperty("id", out JsonElement idElement) ? (idElement.GetString() ?? "") : "";
            string socket = root.TryGetProperty("webSocketDebuggerUrl", out JsonElement socketElement) ? (socketElement.GetString() ?? "") : "";

            if (id.Length == 0 || socket.Length == 0)
            {
                return null;
            }

            return new TargetInfo { Id = id, SocketUrl = socket };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>向调试端点发一个请求并取回文本。</summary>
    private static async Task<string?> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000);

        try
        {
            using var request = new HttpRequestMessage(method, url);
            using HttpResponseMessage response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>借 PowerShell 查全部 chrome 主进程（id + 命令行）。</summary>
    private static async Task<List<(int Pid, string CommandLine)>> QueryChromeMainsAsync(CancellationToken ct)
    {
        var result = new List<(int Pid, string CommandLine)>();
        string command = "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using Process? shell = Process.Start(psi);
        if (shell is null)
        {
            return result;
        }

        string output = await shell.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await shell.WaitForExitAsync(ct).ConfigureAwait(false);

        if (output.Trim().Length == 0)
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                CollectProc(root, result);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    CollectProc(item, result);
                }
            }
        }
        catch (JsonException)
        {
            // 输出不可解析——按未找到处理。
        }

        return result;
    }

    /// <summary>从一条 chrome 进程记录里挑出主进程（跳过渲染 / 辅助子进程）。</summary>
    private static void CollectProc(JsonElement item, List<(int Pid, string CommandLine)> target)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!item.TryGetProperty("ProcessId", out JsonElement idElement) || !idElement.TryGetInt32(out int pid))
        {
            return;
        }

        string commandLine = item.TryGetProperty("CommandLine", out JsonElement line) ? (line.GetString() ?? "") : "";
        if (commandLine.Length == 0)
        {
            return;
        }

        if (commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return;
        }

        target.Add((pid, commandLine));
    }

    /// <summary>从命令行里读出 --user-data-dir 的值（含带引号形态）。</summary>
    private static string ReadProfileDir(string commandLine)
    {
        const string marker = "--user-data-dir=";
        int at = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return "";
        }

        string rest = commandLine.Substring(at + marker.Length);

        // [段1] 子进程带引号形态（"…\5"）——按引号闭合截取
        if (rest.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = rest.IndexOf('"', 1);
            if (end < 0)
            {
                return "";
            }

            return rest.Substring(1, end - 1).Trim();
        }

        // [段2] 主进程无引号形态——截到空格
        int space = rest.IndexOf(' ');
        if (space < 0)
        {
            return rest.Trim();
        }

        return rest.Substring(0, space).Trim();
    }
}
