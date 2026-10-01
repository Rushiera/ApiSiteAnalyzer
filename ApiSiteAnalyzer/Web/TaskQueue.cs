using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 串行任务队列——浏览器重操作（探测 / 拉取 / 打开登录）共用一条通道，
/// 同一时刻只跑一个，避免同一用户目录被并发驱动。
/// </summary>
public sealed class TaskQueue
{
    /// <summary>排队等待上限（毫秒）——前一个浏览器操作卡住时，后来者以超时收场并出声，不无限等待。</summary>
    private const int QueueWaitMs = 120000;

    /// <summary>串行闸门。</summary>
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    /// <summary>排队中 / 运行中的任务数。</summary>
    private int _pending;

    /// <summary>当前排队深度。</summary>
    public int Pending => _pending;

    /// <summary>入队执行一个异步操作。</summary>
    /// <param name="work">操作。</param>
    /// <returns>操作结果。</returns>
    public async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        Interlocked.Increment(ref _pending);
        if (!await _gate.WaitAsync(QueueWaitMs).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _pending);
            throw new TimeoutException("另一个浏览器操作仍在进行中（已等待 " + (QueueWaitMs / 1000) + " 秒）——请稍后重试");
        }

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>入队执行一个异步操作（无返回值）。</summary>
    /// <param name="work">操作。</param>
    /// <returns>异步任务。</returns>
    public async Task RunAsync(Func<Task> work)
    {
        Interlocked.Increment(ref _pending);
        if (!await _gate.WaitAsync(QueueWaitMs).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _pending);
            throw new TimeoutException("另一个浏览器操作仍在进行中（已等待 " + (QueueWaitMs / 1000) + " 秒）——请稍后重试");
        }

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Interlocked.Decrement(ref _pending);
        }
    }
}

/// <summary>面板运行态——采集进度与结果（单站点串行，状态只有一个槽位）。</summary>
public sealed class PanelState
{
    /// <summary>状态读写锁。</summary>
    private readonly object _gate = new object();

    /// <summary>当前站点显示名。</summary>
    private string _site = "";

    /// <summary>阶段：idle / running / done / failed。</summary>
    private string _phase = "idle";

    /// <summary>进度文本。</summary>
    private string _message = "";

    /// <summary>真实余额（quota 单位；0 = 未取到）。</summary>
    private long _balance;

    /// <summary>自动采集是否开启（面板顶部状态显示用）。</summary>
    private bool _autoEnabled;

    /// <summary>自动采集间隔（秒）。</summary>
    private int _autoIntervalSeconds;

    /// <summary>下一次自动采集时刻（yyyy-MM-dd HH:mm:ss；空 = 未排）。</summary>
    private string _autoNextAt = "";

    /// <summary>构造。</summary>
    /// <param name="sites">站点清单（保留）。</param>
    /// <param name="dbPath">库路径（保留）。</param>
    public PanelState(List<Sites.IApiSite> sites, string dbPath)
    {
        _ = sites;
        _ = dbPath;
    }

    /// <summary>是否有采集在跑。</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _phase == "running";
            }
        }
    }

    /// <summary>开始一轮采集。</summary>
    /// <param name="siteName">站点显示名。</param>
    public void Begin(string siteName)
    {
        lock (_gate)
        {
            _site = siteName;
            _phase = "running";
            _message = "开始采集 " + siteName;
        }
    }

    /// <summary>
    /// 抢占一轮采集——「检查是否在跑」与「置为运行中」必须在同一把锁里完成。
    /// 分两步做会留竞态窗口：手动拉取与自动采集同时通过检查，双双驱动同一浏览器会话。
    /// </summary>
    /// <param name="siteName">站点显示名。</param>
    /// <returns>抢到返回 true；已有采集在跑返回 false。</returns>
    public bool TryBegin(string siteName)
    {
        lock (_gate)
        {
            if (_phase == "running")
            {
                return false;
            }

            _site = siteName;
            _phase = "running";
            _message = "开始采集 " + siteName;
            return true;
        }
    }

    /// <summary>推进一条进度。</summary>
    /// <param name="text">进度文本。</param>
    public void Progress(string text)
    {
        lock (_gate)
        {
            if (_phase == "running")
            {
                _message = text;
            }
        }
    }

    /// <summary>完成。</summary>
    /// <param name="text">结果文本。</param>
    public void Done(string text)
    {
        lock (_gate)
        {
            _phase = "done";
            _message = text;
        }
    }

    /// <summary>失败。</summary>
    /// <param name="text">失败原因。</param>
    public void Fail(string text)
    {
        lock (_gate)
        {
            _phase = "failed";
            _message = text;
        }
    }

    /// <summary>记下真实余额。</summary>
    /// <param name="balance">余额（quota 单位）。</param>
    public void SetBalance(long balance)
    {
        lock (_gate)
        {
            _balance = balance;
        }
    }

    /// <summary>记下自动采集状态（面板顶部一眼可见「开没开、下次什么时候」）。</summary>
    /// <param name="enabled">是否开启。</param>
    /// <param name="intervalSeconds">间隔（秒）。</param>
    /// <param name="nextAt">下一次到点时刻。</param>
    public void SetAuto(bool enabled, int intervalSeconds, DateTime nextAt)
    {
        lock (_gate)
        {
            _autoEnabled = enabled;
            _autoIntervalSeconds = intervalSeconds;
            _autoNextAt = enabled ? nextAt.ToString("yyyy-MM-dd HH:mm:ss") : "";
        }
    }

    /// <summary>取当前状态文本（自动采集日志用）。</summary>
    /// <returns>状态文本。</returns>
    public string LastMessage()
    {
        lock (_gate)
        {
            return _message;
        }
    }

    /// <summary>取状态快照。</summary>
    /// <returns>快照对象。</returns>
    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                ok = true,
                phase = _phase,
                site = _site,
                message = _message,
                running = _phase == "running",
                balance = _balance,
                autoEnabled = _autoEnabled,
                autoIntervalSeconds = _autoIntervalSeconds,
                autoNextAt = _autoNextAt,
            };
        }
    }
}
