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
    /// <summary>当前站点键（余额按站点分开记，总览视图要逐站取）。</summary>
    private string _siteId = "";
    /// <summary>各站点最近一次探到的真实余额（quota 单位；键 = 站点键，0 = 未取到）。</summary>
    private readonly Dictionary<string, long> _balances = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>各站点最近一次探到的登录态（键 = 站点键）——总览站点块据此显示「已登录 / 未登录 / 未探测」。</summary>
    private readonly Dictionary<string, LoginSnapshot> _logins = new Dictionary<string, LoginSnapshot>(StringComparer.Ordinal);

    /// <summary>阶段：idle / running / done / failed。</summary>
    private string _phase = "idle";

    /// <summary>进度文本。</summary>
    private string _message = "";

    /// <summary>自动采集是否开启（全局开关；面板顶部状态显示用）。</summary>
    private bool _autoEnabled;

    /// <summary>各站点自动采集运行态（键 = 站点键）——各站独立计时与降级，互不影响。</summary>
    private readonly Dictionary<string, AutoState> _autoStates = new Dictionary<string, AutoState>(StringComparer.Ordinal);

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

    /// <summary>
    /// 抢占一轮采集——「检查是否在跑」与「置为运行中」必须在同一把锁里完成。
    /// 分两步做会留竞态窗口：手动拉取与自动采集同时通过检查，双双驱动同一浏览器会话。
    /// </summary>
    /// <param name="siteId">站点键（余额按站点分开记）。</param>
    /// <param name="siteName">站点显示名。</param>
    /// <returns>抢到返回 true；已有采集在跑返回 false。</returns>
    public bool TryBegin(string siteId, string siteName)
    {
        lock (_gate)
        {
            if (_phase == "running")
            {
                return false;
            }

            _siteId = siteId;
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

    /// <summary>记下某站点的真实余额（按站点分开存——总览视图要逐站显示）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="balance">余额（quota 单位；0 = 未取到）。</param>
    public void SetBalance(string siteId, long balance)
    {
        lock (_gate)
        {
            _balances[siteId] = balance;
        }
    }
    /// <summary>取某站点最近一次探到的真实余额（0 = 未取到）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>余额（quota 单位）。</returns>
    public long BalanceOf(string siteId)
    {
        lock (_gate)
        {
            return _balances.TryGetValue(siteId, out long value) ? value : 0;
        }
    }

    /// <summary>
    /// 记下某站点的登录态（探测 / 采集后调用）——总览站点块据此显示各站是否掉登录。
    /// 直连通道站点的「登录态」等价于 API key 是否有效（该通道没有浏览器登录这回事）。
    /// </summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="loggedIn">是否有效（浏览器通道 = 已登录；直连通道 = key 有效）。</param>
    /// <param name="username">账号名（拿不到传空串）。</param>
    /// <param name="message">未登录 / 无效的原因（正常时传空串）。</param>
    public void SetLogin(string siteId, bool loggedIn, string username, string message)
    {
        lock (_gate)
        {
            _logins[siteId] = new LoginSnapshot
            {
                LoggedIn = loggedIn,
                Username = username,
                Message = message,
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }
    }

    /// <summary>取某站点最近一次探到的登录态（null = 从未探测过——如实显示「未探测」，不冒充已登录）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <returns>登录态快照；从未探测返回 null。</returns>
    public LoginSnapshot? LoginOf(string siteId)
    {
        lock (_gate)
        {
            return _logins.TryGetValue(siteId, out LoginSnapshot? value) ? value : null;
        }
    }

    /// <summary>记下自动采集总开关（各站共用一个开关；间隔与降级各站独立）。</summary>
    /// <param name="enabled">是否开启。</param>
    public void SetAutoEnabled(bool enabled)
    {
        lock (_gate)
        {
            _autoEnabled = enabled;
        }
    }

    /// <summary>记下一个站点的自动采集状态（面板顶部与站点块据此显示间隔 / 降级 / 异常 / 停采）。</summary>
    /// <param name="siteId">站点键。</param>
    /// <param name="state">该站运行态快照（内部复制一份——调用方随后改动不影响已公布的值）。</param>
    public void SetAuto(string siteId, AutoState state)
    {
        lock (_gate)
        {
            _autoStates[siteId] = new AutoState
            {
                IntervalSeconds = state.IntervalSeconds,
                EmptyRounds = state.EmptyRounds,
                FailStreak = state.FailStreak,
                Stopped = state.Stopped,
                Error = state.Error,
                NextAt = _autoEnabled ? state.NextAt : "",
            };
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
            var balances = new Dictionary<string, long>(_balances, StringComparer.Ordinal);
            long current = balances.TryGetValue(_siteId, out long value) ? value : 0;
            var autoSites = new Dictionary<string, AutoState>(_autoStates, StringComparer.Ordinal);
            var logins = new Dictionary<string, LoginSnapshot>(_logins, StringComparer.Ordinal);
            return new
            {
                ok = true,
                phase = _phase,
                site = _site,
                siteId = _siteId,
                message = _message,
                running = _phase == "running",
                balance = current,
                balances = balances,
                logins = logins,
                autoEnabled = _autoEnabled,
                autoSites = autoSites,
            };
        }
    }
}

/// <summary>
/// 一个站点的自动采集运行态——各站独立计时与降级，互不影响。
/// 只作对外快照（面板显示用）：降级计数与停采标志都在内存，不落盘。
/// </summary>
public sealed class AutoState
{
    /// <summary>当前生效间隔（秒——降级后是翻倍值，不是基准值）。</summary>
    public int IntervalSeconds { get; set; }

    /// <summary>连续空采轮数（含失败轮；0 = 未降级）。</summary>
    public int EmptyRounds { get; set; }

    /// <summary>连续失败次数（成功即清零）。</summary>
    public int FailStreak { get; set; }

    /// <summary>是否已停采（连续失败达上限）。</summary>
    public bool Stopped { get; set; }

    /// <summary>最近一次失败原因（空 = 正常）。</summary>
    public string Error { get; set; } = "";

    /// <summary>下一次到点时刻（yyyy-MM-dd HH:mm:ss；空 = 未排）。</summary>
    public string NextAt { get; set; } = "";
}

/// <summary>
/// 一个站点的登录态快照（面板显示用）——最近一次探测 / 采集时的结论。
/// 浏览器通道站点 = 是否已登录；直连通道站点 = API key 是否有效（该通道没有浏览器登录这回事）。
/// </summary>
public sealed class LoginSnapshot
{
    /// <summary>是否有效（浏览器通道 = 已登录；直连通道 = key 有效）。</summary>
    public bool LoggedIn { get; set; }

    /// <summary>账号名（拿不到留空）。</summary>
    public string Username { get; set; } = "";

    /// <summary>未登录 / 无效时的原因（正常时为空）。</summary>
    public string Message { get; set; } = "";

    /// <summary>本次结论的时刻（yyyy-MM-dd HH:mm:ss）。</summary>
    public string CheckedAt { get; set; } = "";
}
