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

    /// <summary>下一次到点时刻（本地时间）。</summary>
    private DateTime _nextRunAt = DateTime.MinValue;
    /// <summary>
    /// 连续「没采到新东西」的轮数——决定下次间隔翻几倍（每轮翻一倍，上限 30 分钟）。
    /// **只在内存**（不落盘）：`settings.json` 里存的始终是基准间隔，重启回到基准。
    /// </summary>
    private int _emptyRounds;

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
    }

    /// <summary>重排下一次到点时刻（设置改动后调用——从此刻起重新计时）。</summary>
    public void Reset()
    {
        _emptyRounds = 0;
        _nextRunAt = DateTime.Now.AddSeconds(_settings.AutoIntervalSeconds);
    }

    /// <summary>
    /// 主循环——每秒对一次表：开关关着就把到点时刻往后推（打开后从那一刻重新计时）；
    /// 开着且到点且当前没有采集在跑，就跑一轮（增量口径跟随设置）。
    /// </summary>
    /// <param name="ct">取消令牌（面板停机即退出）。</param>
    /// <returns>异步任务。</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        Reset();

        // [段1] 立刻对外公布一次自动采集状态——否则面板首屏（早于第一次 tick）会显示默认的「关」，
        //        用户看到的是「设置开着、顶部说关着」，持续约一秒的误导
        _state.SetAuto(_settings.AutoEnabled, EffectiveIntervalSeconds(), _emptyRounds, _nextRunAt);

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

            _state.SetAuto(_settings.AutoEnabled, EffectiveIntervalSeconds(), _emptyRounds, _nextRunAt);

            if (!_settings.AutoEnabled)
            {
                // [段2] 关着——到点时刻始终保持在「此刻 + 间隔」之外，重新打开后从那一刻起算
                _nextRunAt = DateTime.Now.AddSeconds(EffectiveIntervalSeconds());
                continue;
            }

            if (DateTime.Now < _nextRunAt)
            {
                continue;
            }

            // [段3] 上一轮还没跑完（手动拉取 / 自动上一轮）——不排队、不打断，稍后再看
            if (_state.Running)
            {
                _nextRunAt = DateTime.Now.AddSeconds(BusyRetrySeconds);
                continue;
            }

            // [段4] 跑一轮——回执决定降级计数，下一次到点时刻按新计数重排
            await RunOnceAsync().ConfigureAwait(false);
            _nextRunAt = DateTime.Now.AddSeconds(EffectiveIntervalSeconds());
        }
    }

    /// <summary>跑一轮自动采集（失败出声——状态条上看得见）。</summary>
    /// <returns>异步任务。</returns>
    private async Task RunOnceAsync()
    {
        if (_sites.Count == 0)
        {
            return;
        }

        IApiSite site = _sites[0];
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

            // [段5] 频率降级——成功但本轮没采到新东西 → 计数 +1（下次间隔翻倍）；采到新东西 → 清零（回基准）。
            //        失败不改变计数：失败路径不得改变状态，且失败后应尽快重试（用户可能刚登录完在等）
            if (summary.Ok)
            {
                if (summary.Added == 0)
                {
                    _emptyRounds++;
                    Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 本轮无新增（连续 " + _emptyRounds +
                        " 轮）——下次间隔 " + EffectiveIntervalSeconds() + " 秒");
                }
                else
                {
                    _emptyRounds = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _state.Fail("自动采集失败：" + ex.Message);
        }
    }
    /// <summary>
    /// 本轮生效的采集间隔（秒）——基准间隔按「连续空采轮数」逐轮翻倍，上限 30 分钟。
    /// 降级属运行态：计数只在内存，不落盘（`settings.json` 里始终是基准间隔）。
    /// </summary>
    /// <returns>生效间隔（秒）。</returns>
    private int EffectiveIntervalSeconds()
    {
        int seconds = _settings.AutoIntervalSeconds;
        for (int i = 0; i < _emptyRounds; i++)
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
}
