using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ApiSiteAnalyzer.Browser;

/// <summary>
/// 极简 CDP 会话——通过 DevTools 协议在页面内执行 JS。
/// **凭据不出浏览器**：本类只回传页面数据，不读取、不落盘任何 cookie / token。
/// </summary>
public sealed class CdpSession : IAsyncDisposable
{
    /// <summary>自有 chrome 进程（null = 外部受控实例，Dispose 时不结束它）。</summary>
    private readonly ChromeProcess? _chrome;

    /// <summary>页面级调试通道。</summary>
    private readonly ClientWebSocket _socket;

    /// <summary>会话取消源。</summary>
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    /// <summary>等待回执的请求表（id → 完成源）。</summary>
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending =
        new Dictionary<int, TaskCompletionSource<JsonElement>>();

    /// <summary>请求表读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>请求号计数器。</summary>
    private int _nextId;

    /// <summary>消息泵任务。</summary>
    private Task? _pump;
    /// <summary>单条 CDP 指令的等待上限（毫秒）——超时即失败，不让调用方永久挂起。
    /// 取值须高于页面侧最坏耗时（刷新 15s + 取数 15s），否则会把正常慢请求误判成挂死。</summary>
    private const int CommandTimeoutMs = 60000;
    /// <summary>通道是否已判定不可用（消息泵退出后置位）——置位后指令立即失败，不静默挂起。</summary>
    private volatile bool _closed;

    /// <summary>以进程句柄与已连通的 ws 构造会话。</summary>
    /// <param name="chrome">自有进程（null = 外部实例）。</param>
    /// <param name="socket">已连通的页面级 ws。</param>
    private CdpSession(ChromeProcess? chrome, ClientWebSocket socket)
    {
        _chrome = chrome;
        _socket = socket;
    }

    /// <summary>启动 headless chrome 并建立页面级 CDP 连接。</summary>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="timeoutMs">启动与连接超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已连接的 CDP 会话。</returns>
    public static async Task<CdpSession> StartAsync(string chromePath, string profileDir, int timeoutMs, CancellationToken ct)
    {
        ChromeProcess chrome = await ChromeLauncher.StartHeadlessAsync(chromePath, profileDir, timeoutMs, ct).ConfigureAwait(false);

        try
        {
            string pageSocket = await FindPageSocketAsync(chrome.Port, timeoutMs, ct).ConfigureAwait(false);
            return await ConnectCoreAsync(chrome, pageSocket, timeoutMs, ct).ConfigureAwait(false);
        }
        catch
        {
            chrome.Dispose();
            throw;
        }
    }

    /// <summary>连接已有浏览器实例的页面通道（外部实例——Dispose 不结束浏览器）。</summary>
    /// <param name="pageSocketUrl">页面级调试端点（ws://…）。</param>
    /// <param name="timeoutMs">连接超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已连接的 CDP 会话。</returns>
    public static Task<CdpSession> ConnectAsync(string pageSocketUrl, int timeoutMs, CancellationToken ct)
    {
        return ConnectCoreAsync(null, pageSocketUrl, timeoutMs, ct);
    }

    /// <summary>建立页面级 CDP 连接并启动消息泵。</summary>
    private static async Task<CdpSession> ConnectCoreAsync(ChromeProcess? chrome, string pageSocketUrl, int timeoutMs, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeoutMs);
        await socket.ConnectAsync(new Uri(pageSocketUrl), connectCts.Token).ConfigureAwait(false);

        var session = new CdpSession(chrome, socket);
        session._pump = Task.Run(() => session.PumpAsync());
        return session;
    }

    /// <summary>打开页面（不等待加载完成）。</summary>
    /// <param name="url">目标地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task NavigateAsync(string url, CancellationToken ct)
    {
        return SendAsync("Page.navigate", new { url }, ct);
    }

    /// <summary>在文档创建前注入脚本（用于 hook fetch / XHR——须在导航之前调用）。</summary>
    /// <param name="source">脚本源码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task AddInitScriptAsync(string source, CancellationToken ct)
    {
        return SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source }, ct);
    }

    /// <summary>启用页面域（导航事件）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public Task EnablePageAsync(CancellationToken ct)
    {
        return SendAsync("Page.enable", null, ct);
    }

    /// <summary>在页面内求值并取回字符串结果。</summary>
    /// <param name="expression">JS 表达式（建议自带 JSON.stringify）。</param>
    /// <param name="awaitPromise">是否等待 Promise 完成。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>结果字符串；无值返回空串。</returns>
    public async Task<string> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct)
    {
        JsonElement response = await SendAsync("Runtime.evaluate", new
        {
            expression,
            awaitPromise,
            returnByValue = true,
        }, ct).ConfigureAwait(false);

        if (!response.TryGetProperty("result", out JsonElement payload))
        {
            return "";
        }

        if (payload.TryGetProperty("exceptionDetails", out JsonElement error))
        {
            throw new InvalidOperationException("页面脚本异常：" + error.ToString());
        }

        if (!payload.TryGetProperty("result", out JsonElement value))
        {
            return "";
        }

        if (value.TryGetProperty("value", out JsonElement direct))
        {
            return direct.ValueKind == JsonValueKind.String ? (direct.GetString() ?? "") : direct.ToString();
        }

        return "";
    }

    /// <summary>关闭会话（先关 WS，再结束自有 chrome 进程树）。</summary>
    /// <returns>异步任务。</returns>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // 浏览器已断开——忽略
        }
        catch (OperationCanceledException)
        {
            // 关闭超时——忽略
        }

        _socket.Dispose();

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 泵已取消——忽略
            }
        }

        // [段1] 只结束自有进程——外部受控实例由调用方继续复用
        if (_chrome is not null)
        {
            _chrome.Dispose();
        }

        _cts.Dispose();
    }

    /// <summary>发一条 CDP 指令并等待回执。</summary>
    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        if (_closed)
        {
            throw new InvalidOperationException("CDP 通道已关闭（浏览器窗口或页面已失效）");
        }

        int id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            id = ++_nextId;
            _pending[id] = tcs;
        }

        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            // [段1] 等待有上限——通道静默 / 浏览器退出时以超时收场，不让串行队列永久卡死
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(CommandTimeoutMs);
            try
            {
                using (wait.Token.Register(() => tcs.TrySetCanceled(wait.Token)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("CDP 指令超时（" + method + " 未在 " + CommandTimeoutMs + " ms 内收到回执）");
            }
        }
        finally
        {
            // [段2] 无论成败都摘掉等待位——失败不累积请求表
            lock (_gate)
            {
                _pending.Remove(id);
            }
        }
    }
    /// <summary>把未决指令全部标记为失败（通道已不可用）——失败必须可见，不静默挂起。</summary>
    private void FailPending()
    {
        _closed = true;

        List<TaskCompletionSource<JsonElement>> waiters;
        lock (_gate)
        {
            waiters = new List<TaskCompletionSource<JsonElement>>(_pending.Values);
            _pending.Clear();
        }

        foreach (TaskCompletionSource<JsonElement> waiter in waiters)
        {
            waiter.TrySetException(new InvalidOperationException("CDP 通道已关闭（浏览器窗口或页面已失效）"));
        }
    }

    /// <summary>消息泵——按 id 配对回执，事件消息丢弃。</summary>
    private async Task PumpAsync()
    {
        var buffer = new byte[512 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                Dispatch(sb.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭路径
        }
        catch (WebSocketException)
        {
            // 浏览器已退出——收尾
        }
        finally
        {
            // [段1] 通道一旦结束，未决指令立即失败——不让调用方在死通道上永久等待（串行队列会被它永久占用）
            FailPending();
        }
    }

    /// <summary>分派一条回执消息。</summary>
    private void Dispatch(string text)
    {
        TaskCompletionSource<JsonElement>? waiter = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id))
            {
                return;
            }

            lock (_gate)
            {
                if (_pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? found))
                {
                    waiter = found;
                    _pending.Remove(id);
                }
            }

            if (waiter is null)
            {
                return;
            }

            JsonElement clone = root.Clone();
            if (clone.TryGetProperty("error", out JsonElement error))
            {
                waiter.TrySetException(new InvalidOperationException("CDP 错误：" + error.ToString()));
            }
            else
            {
                waiter.TrySetResult(clone);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 消息——忽略
        }
    }

    /// <summary>从调试端口找页面级 ws 端点。</summary>
    private static async Task<string> FindPageSocketAsync(int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        string listUrl = "http://127.0.0.1:" + port + "/json/list";

        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                string json = await client.GetStringAsync(listUrl, ct).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                foreach (JsonElement target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out JsonElement type) &&
                        type.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out JsonElement ws))
                    {
                        return ws.GetString() ?? "";
                    }
                }
            }
            catch (HttpRequestException)
            {
                // 端口尚未就绪——重试
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("未找到可用的页面调试端点");
    }
}
