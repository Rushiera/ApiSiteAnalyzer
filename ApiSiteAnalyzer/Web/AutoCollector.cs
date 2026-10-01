using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ApiSiteAnalyzer.Collect;
using ApiSiteAnalyzer.Sites;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 自动采集器——按设置里的间隔定时拉取，**跑在服务端**。
/// 为什么不在页面里计时：设置存在 `data/settings.json`（跨重启生效），若计时器挂在页面上，
/// 关掉页面就悄悄不跑了——「设置说开着、实际没跑」正是静默失败。计时归服务端，页面只负责显示与改设置。
/// </summary>
public sealed class AutoCollector
{
    /// <summary>心跳间隔（毫秒）——够密以便设置改动及时生效，又不占 CPU。</summary>
    private const int TickMs = 1000;

    /// <summary>上一轮采集仍在跑时的复查间隔（秒）。</summary>
    private const int BusyRetrySeconds = 5;
    /// <summary>降级上限（秒）——连续空采时下次间隔逐轮翻倍，最长不超过 30 分钟。</summary>
    private const int MaxBackoffSeconds = 1800;
    /// <summary>连续失败上限——达到即停该站的自动采集（连续失败多为登录态失效，继续重试没有意义）。</summary>
    private const int MaxFailStreak = 3;

    /// <summary>站点会话。</summary>
    private readonly SiteSession _session;

    /// <summary>站点清单。</summary>
    private readonly List<IApiSite> _sites;

    /// <summary>库路径。</summary>
    private readonly string _dbPath;

    /// <summary>面板运行态。</summary>
    private readonly PanelState _state;

    /// <summary>面板设置。</summary>
    private readonly PanelSettings _settings;

    /// <summary>各站点的计时器（按站点清单顺序）——各站独立计时与降级，互不影响。</summary>
    private readonly List<AutoSiteTimer> _timers = new List<AutoSiteTimer>();

    /// <summary>构造自动采集器。</summary>
    /// <param name="session">站点会话。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="dbPath">库路径。</param>
    /// <param name="state">面板运行态。</param>
    /// <param name="settings">面板设置。</param>
    public AutoCollector(SiteSession session, List<IApiSite> sites, string dbPath, PanelState state, PanelSettings settings)
    {
        _session = session;
        _sites = sites;
        _dbPath = dbPath;
        _state = state;
        _settings = settings;

        // [段1] 每个站点一个计时器——各站独立计时与降级，互不影响
        foreach (IApiSite site in sites)
        {
            _timers.Add(new AutoSiteTimer { Site = site });
        }
    }

    /// <summary>全部站点重排下一次到点时刻并清空降级 / 失败计数（设置改动后调用——从此刻起重新开始）。</summary>
    public void Reset()
    {
        foreach (AutoSiteTimer timer in _timers)
        {
            timer.EmptyRounds = 0;
            timer.FailStreak = 0;
            timer.Stopped = false;
            timer.Error = "";
            timer.NextRunAt = DateTime.Now.AddSeconds(_settings.AutoIntervalSeconds);
        }

        Publish();
    }

    /// <summary>
    /// 主循环——每秒对一次表：开关关着就把各站的到点时刻往后推（打开后从那一刻重新计时）；
    /// 开着则逐站检查——某站到点、未停采、且当前没有采集在跑，就跑该站一轮（增量口径跟随设置）。
    /// 各站独立计时与降级，互不影响。
    /// </summary>
    /// <param name="ct">取消令牌（面板停机即退出）。</param>
    /// <returns>异步任务。</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        Reset();

        // [段1] 立刻对外公布一次状态——否则面板首屏（早于第一次 tick）会显示默认的「关」，
        //        用户看到的是「设置开着、顶部说关着」，持续约一秒的误导
        Publish();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Publish();

            if (!_settings.AutoEnabled)
            {
                // [段2] 关着——各站到点时刻始终保持在「此刻 + 间隔」之外，重新打开后从那一刻起算
                foreach (AutoSiteTimer timer in _timers)
                {
                    timer.NextRunAt = DateTime.Now.AddSeconds(EffectiveSeconds(timer));
                }

                continue;
            }

            // [段3] 逐站对表——各站到点独立、间隔独立，一站降级不影响别站；
            //        采集动作本身仍串行（状态只有一个槽位，避免同一浏览器会话被并发驱动）
            foreach (AutoSiteTimer timer in _timers)
            {
                if (timer.Stopped)
                {
                    continue;
                }

                if (DateTime.Now < timer.NextRunAt)
                {
                    continue;
                }

                if (_state.Running)
                {
                    timer.NextRunAt = DateTime.Now.AddSeconds(BusyRetrySeconds);
                    continue;
                }

                await RunOnceAsync(timer).ConfigureAwait(false);
                timer.NextRunAt = DateTime.Now.AddSeconds(EffectiveSeconds(timer));
            }
        }
    }

    /// <summary>跑一个站点的一轮自动采集（失败出声——状态条与总览站点块上看得见）。</summary>
    /// <param name="timer">站点计时器（该站独立状态）。</param>
    /// <returns>异步任务。</returns>
    private async Task RunOnceAsync(AutoSiteTimer timer)
    {
        IApiSite site = timer.Site;
        if (!_state.TryBegin(site.Id, site.DisplayName + "（自动）"))
        {
            return;
        }

        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 自动采集开始：" + site.DisplayName +
            (_settings.Incremental ? "（增量）" : "（全量）"));
        _state.SetBalance(site.Id, 0);

        try
        {
            FetchSummary summary = await ServeRunner.FetchSiteAsync(_session, site, _dbPath, _state, _settings.Incremental).ConfigureAwait(false);
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 自动采集结束：" + _state.LastMessage());

            // [段1] 采到新数据 → 回基准；没采到新数据 → 下次间隔翻倍；失败 → 同样按「没采到」翻倍，并计失败数
            if (!summary.Ok)
            {
                MarkFailure(timer, summary.NotLoggedIn ? "未登录" : summary.Error);
            }
            else if (summary.Added == 0)
            {
                timer.EmptyRounds++;
                timer.FailStreak = 0;
                timer.Error = "";
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + site.DisplayName +
                    " 本轮无新增（连续 " + timer.EmptyRounds + " 轮）——下次间隔 " + EffectiveSeconds(timer) + " 秒");
            }
            else
            {
                timer.EmptyRounds = 0;
                timer.FailStreak = 0;
                timer.Error = "";
            }
        }
        catch (Exception ex)
        {
            _state.Fail("自动采集失败：" + ex.Message);
            MarkFailure(timer, ex.Message);
        }

        Publish();
    }
    /// <summary>
    /// 某站本轮生效的采集间隔（秒）——基准间隔按「连续空采轮数」逐轮翻倍，上限 30 分钟。
    /// 降级属运行态：计数只在内存，不落盘（`settings.json` 里始终是基准间隔）。
    /// </summary>
    /// <param name="timer">站点计时器。</param>
    /// <returns>生效间隔（秒）。</returns>
    private int EffectiveSeconds(AutoSiteTimer timer)
    {
        int seconds = _settings.AutoIntervalSeconds;
        for (int i = 0; i < timer.EmptyRounds; i++)
        {
            if (seconds >= MaxBackoffSeconds)
            {
                return MaxBackoffSeconds;
            }

            seconds = seconds * 2;
        }

        if (seconds > MaxBackoffSeconds)
        {
            return MaxBackoffSeconds;
        }

        return seconds;
    }
    /// <summary>把各站的运行态公布给面板（顶部状态条与总览站点块都读它）。</summary>
    private void Publish()
    {
        _state.SetAutoEnabled(_settings.AutoEnabled);

        foreach (AutoSiteTimer timer in _timers)
        {
            _state.SetAuto(timer.Site.Id, new AutoState
            {
                IntervalSeconds = EffectiveSeconds(timer),
                EmptyRounds = timer.EmptyRounds,
                FailStreak = timer.FailStreak,
                Stopped = timer.Stopped,
                Error = timer.Error,
                NextAt = timer.Stopped ? "" : timer.NextRunAt.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }
    }
    /// <summary>
    /// 记一次采集失败——失败同样「没采到数据」：降级计数 +1（下次间隔翻倍），连续失败达上限即停该站。
    /// 停采只停自动采集，不动库内数据——面板继续用本地历史已有的数据展示。
    /// </summary>
    /// <param name="timer">站点计时器。</param>
    /// <param name="reason">失败原因。</param>
    private void MarkFailure(AutoSiteTimer timer, string reason)
    {
        timer.EmptyRounds++;
        timer.FailStreak++;
        timer.Error = reason;

        if (timer.FailStreak >= MaxFailStreak)
        {
            timer.Stopped = true;
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + timer.Site.DisplayName +
                " 连续 " + MaxFailStreak + " 次采集失败——该站自动采集已停（" + reason + "）");
            return;
        }

        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + timer.Site.DisplayName +
            " 采集失败：" + reason + "（连续 " + timer.FailStreak + " 次）——下次间隔 " + EffectiveSeconds(timer) + " 秒");
    }
}

/// <summary>
/// 一个站点的自动采集计时与运行态——各站独立计时、独立降级，互不影响。
/// 全部在内存（不落盘）：重启回到基准间隔、失败计数清零。
/// </summary>
internal sealed class AutoSiteTimer
{
    /// <summary>站点适配器。</summary>
    public IApiSite Site { get; set; } = null!;

    /// <summary>下一次到点时刻（本地时间）。</summary>
    public DateTime NextRunAt { get; set; } = DateTime.MinValue;

    /// <summary>连续空采轮数（含失败轮）——决定下次间隔翻几倍。</summary>
    public int EmptyRounds { get; set; }

    /// <summary>连续失败次数（成功即清零）。</summary>
    public int FailStreak { get; set; }

    /// <summary>是否已停采（连续失败达上限）。</summary>
    public bool Stopped { get; set; }

    /// <summary>最近一次失败原因（空 = 正常）。</summary>
    public string Error { get; set; } = "";
}
