using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using ApiSiteAnalyzer.Sites;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 本机 serve 自举——面板启动时探测本机常驻服务（work 壳的 serve）是否在跑，未起则拉起。
/// <para>
/// 判据（2026-11-05 · design-workserve §六）：**只起 apis 一个进程即全开**——中转站 iframe 与第四站取数都挂在 18100。
/// </para>
/// <para>
/// 三条边界：① 只认回环地址 + 恒直连站（远端站点一律不碰）；② 已在跑则不动（重复拉起会把 serve 断电）；
/// ③ 拉起失败必须出声——结论落 <c>data/run/serve-launch.json</c> 并进核心快照的 <c>serve</c> 字段，不静默当成功。
/// </para>
/// </summary>
public static class ServeLauncher
{
    /// <summary>
    /// 壳侧存活探针路径——主口 18100 的轻量 GET（实测 186 字节）。
    /// 不用 <c>/api/healthz</c>：那条路由在分支口（18102+）上，主口回 404（2026-11-05 实测）。
    /// </summary>
    private const string HealthPath = "/api/service/upstream";

    /// <summary>单次探活超时——启动路径上不能久等。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>拉起后等待就绪的上限（壳起服务约 1–3 s；超时不判失败，只如实说出）。</summary>
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(6);

    /// <summary>就绪轮询间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>serve 可执行文件相对配置目录的路径。</summary>
    private const string ServeExeRel = "apis/WorkBuddyShell.exe";

    /// <summary>
    /// 确保本机 serve 在跑——已在跑返回「已在运行」，否则拉起并等就绪。
    /// </summary>
    /// <param name="sites">站点清单（从中找回环地址的恒直连站）。</param>
    /// <param name="appRoot">config.json 所在目录（serve 运行目录 = 其下 apis/）。</param>
    /// <param name="runDir">data/run（结论落点）。</param>
    /// <returns>结论文本（进核心快照的 serve 字段）。</returns>
    public static string EnsureUp(IReadOnlyList<IApiSite> sites, string appRoot, string runDir)
    {
        string baseUrl = FindLocalBaseUrl(sites);
        if (baseUrl.Length == 0)
        {
            return Record(runDir, "", "skip", true, "无本机直连站——跳过 serve 自举");
        }

        using var client = new HttpClient { Timeout = ProbeTimeout };

        if (Probe(client, baseUrl, out _))
        {
            return Record(runDir, baseUrl, "already", true, "已在运行：" + baseUrl);
        }

        string exe = Path.Combine(appRoot, ServeExeRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine("serve 自举跳过：找不到 " + exe);
            return Record(runDir, baseUrl, "missing-exe", false, "未找到 apis/WorkBuddyShell.exe——需人工起 serve");
        }

        int pid;
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? appRoot,
            };
            info.ArgumentList.Add("--serve");
            Process? process = Process.Start(info);
            pid = process == null ? 0 : process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            || ex is System.ComponentModel.Win32Exception || ex is IOException)
        {
            Console.Error.WriteLine("serve 自举失败：" + ex.GetType().Name + ": " + ex.Message);
            return Record(runDir, baseUrl, "failed", false, "拉起失败：" + ex.Message);
        }

        DateTime deadline = DateTime.UtcNow + ReadyWait;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(PollInterval);
            if (Probe(client, baseUrl, out _))
            {
                return Record(runDir, baseUrl, "started", true, "已拉起并就绪（pid " + pid + "）：" + baseUrl);
            }
        }

        int seconds = (int)ReadyWait.TotalSeconds;
        Console.Error.WriteLine("serve 自举：已拉起 pid " + pid + "，" + seconds + " 秒内未就绪");
        return Record(runDir, baseUrl, "started-slow", false,
            "已拉起（pid " + pid + "），" + seconds + " 秒内未就绪——稍后由站点表反映");
    }

    /// <summary>从站点清单里找回环地址的恒直连站（没有返回空串）。</summary>
    /// <param name="sites">站点清单。</param>
    /// <returns>站点根地址（空 = 无本机站）。</returns>
    private static string FindLocalBaseUrl(IReadOnlyList<IApiSite> sites)
    {
        foreach (IApiSite site in sites)
        {
            if (site is not IDirectSite direct || !direct.AlwaysDirect)
            {
                continue;
            }

            if (Uri.TryCreate(site.BaseUrl, UriKind.Absolute, out Uri? uri) && uri.IsLoopback)
            {
                return site.BaseUrl;
            }
        }

        return "";
    }

    /// <summary>探活——任意 2xx 视为在跑；连接拒绝 / 超时 / 非 2xx 都算未起（原因如实带回）。</summary>
    /// <param name="client">探活用的客户端。</param>
    /// <param name="baseUrl">站点根地址。</param>
    /// <param name="error">失败原因（成功时为空）。</param>
    /// <returns>true = 在跑。</returns>
    private static bool Probe(HttpClient client, string baseUrl, out string error)
    {
        error = "";
        try
        {
            using HttpResponseMessage response = client.GetAsync(baseUrl.TrimEnd('/') + HealthPath).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            error = "探活返回 " + (int)response.StatusCode;
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException
            || ex is InvalidOperationException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>落一条自举结论（失败必须可见——文件与控制台各一份）。</summary>
    /// <param name="runDir">data/run 目录。</param>
    /// <param name="baseUrl">站点根地址。</param>
    /// <param name="action">动作：already / started / started-slow / missing-exe / failed / skip。</param>
    /// <param name="ok">是否达成预期。</param>
    /// <param name="message">结论文本。</param>
    /// <returns>结论文本（供快照复用）。</returns>
    private static string Record(string runDir, string baseUrl, string action, bool ok, string message)
    {
        try
        {
            Directory.CreateDirectory(runDir);
            var payload = new
            {
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                baseUrl,
                action,
                ok,
                message,
                panelPid = Environment.ProcessId,
            };
            File.WriteAllText(Path.Combine(runDir, "serve-launch.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine("serve-launch.json 落盘失败：" + ex.Message);
        }

        return message;
    }
}
