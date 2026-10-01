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
        _state.SetAuto(_settings.AutoEnabled, _settings.AutoIntervalSeconds, _nextRunAt);

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

            _state.SetAuto(_settings.AutoEnabled, _settings.AutoIntervalSeconds, _nextRunAt);

            if (!_settings.AutoEnabled)
            {
                // [段2] 关着——到点时刻始终保持在「此刻 + 间隔」之外，重新打开后从那一刻起算
                _nextRunAt = DateTime.Now.AddSeconds(_settings.AutoIntervalSeconds);
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

            _nextRunAt = DateTime.Now.AddSeconds(_settings.AutoIntervalSeconds);
            await RunOnceAsync().ConfigureAwait(false);
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
        if (!_state.TryBegin(site.DisplayName + "（自动）"))
        {
            return;
        }

        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 自动采集开始：" + site.DisplayName +
            (_settings.Incremental ? "（增量）" : "（全量）"));
        _state.SetBalance(0);

        try
        {
            await ServeRunner.FetchSiteAsync(_session, site, _dbPath, _state, _settings.Incremental).ConfigureAwait(false);
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 自动采集结束：" + _state.LastMessage());
        }
        catch (Exception ex)
        {
            _state.Fail("自动采集失败：" + ex.Message);
        }
    }
}
