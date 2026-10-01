using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ApiSiteAnalyzer.Browser;

/// <summary>
/// chrome 进程启动器——命令行拼装 + 从 stderr 读回 DevTools 端点，并持续抽干 stderr。
/// </summary>
public static class ChromeLauncher
{
    /// <summary>chrome 在 stderr 里报告调试端点的标记。</summary>
    private const string EndpointMarker = "DevTools listening on ";

    /// <summary>启动 headless 临时实例。</summary>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="timeoutMs">等待调试端点的超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已启动进程 + 调试端口。</returns>
    public static Task<ChromeProcess> StartHeadlessAsync(string chromePath, string profileDir, int timeoutMs, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--remote-debugging-port=0",
            "--remote-allow-origins=*",
            "--user-data-dir=" + profileDir,
            "about:blank",
        };

        return StartAsync(chromePath, args, timeoutMs, ct);
    }

    /// <summary>启动受控可见窗口——保留调试端口，登录与采集都复用它。</summary>
    /// <param name="chromePath">chrome.exe 路径。</param>
    /// <param name="profileDir">浏览器用户数据目录。</param>
    /// <param name="url">打开的地址。</param>
    /// <param name="timeoutMs">等待调试端点的超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已启动进程 + 调试端口。</returns>
    public static Task<ChromeProcess> StartWindowAsync(string chromePath, string profileDir, string url, int timeoutMs, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-first-run",
            "--no-default-browser-check",
            "--remote-debugging-port=0",
            "--remote-allow-origins=*",
            "--user-data-dir=" + profileDir,
            url,
        };

        return StartAsync(chromePath, args, timeoutMs, ct);
    }

    /// <summary>按参数启动 chrome 并读回调试端点。</summary>
    private static async Task<ChromeProcess> StartAsync(string chromePath, List<string> args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = chromePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("chrome 启动失败：" + chromePath);
        }

        try
        {
            string endpoint = await ReadEndpointAsync(process, timeoutMs, ct).ConfigureAwait(false);
            var chrome = new ChromeProcess(process, ParsePort(endpoint), endpoint);
            chrome.BeginDrain();
            return chrome;
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>从 stderr 读回 DevTools 端点。</summary>
    private static async Task<string> ReadEndpointAsync(Process process, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        while (true)
        {
            string? line = await process.StandardError.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    "chrome 未报告 DevTools 端点（进程提前退出——该用户目录可能已被其它浏览器实例占用）");
            }

            int at = line.IndexOf(EndpointMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                return line.Substring(at + EndpointMarker.Length).Trim();
            }
        }
    }

    /// <summary>从调试端点取端口。</summary>
    private static int ParsePort(string endpoint)
    {
        var uri = new Uri(endpoint);
        return uri.Port;
    }

    /// <summary>结束进程树（清理用，失败不抛）。</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出。
        }
        catch (Win32Exception)
        {
            // 无权结束或进程已消失。
        }
    }
}

/// <summary>已启动的 chrome 进程 + 调试端口（Dispose 结束整棵进程树）。</summary>
public sealed class ChromeProcess : IDisposable
{
    /// <summary>chrome 主进程。</summary>
    private readonly Process _process;

    /// <summary>stderr 抽干任务（防止管道写满把浏览器阻塞）。</summary>
    private Task? _drain;

    /// <summary>以进程句柄与调试端点构造。</summary>
    /// <param name="process">chrome 主进程。</param>
    /// <param name="port">调试端口。</param>
    /// <param name="endpoint">browser 级调试端点。</param>
    internal ChromeProcess(Process process, int port, string endpoint)
    {
        _process = process;
        Port = port;
        Endpoint = endpoint;
    }

    /// <summary>调试端口。</summary>
    public int Port { get; }

    /// <summary>browser 级调试端点（ws://…）。</summary>
    public string Endpoint { get; }

    /// <summary>进程 id（进程已退出返回 0）。</summary>
    public int Pid
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    /// <summary>开始抽干 stderr——避免管道写满把 chrome 阻塞。</summary>
    internal void BeginDrain()
    {
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>持续读走 stderr 内容（丢弃），避免管道写满。</summary>
    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                string? line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // 管道随进程释放——抽干结束。
        }
        catch (InvalidOperationException)
        {
            // 进程未重定向或已退出——抽干结束。
        }
    }

    /// <summary>结束进程树并释放句柄。</summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出。
        }
        catch (Win32Exception)
        {
            // 无权结束或进程已消失。
        }

        _process.Dispose();
    }
}
