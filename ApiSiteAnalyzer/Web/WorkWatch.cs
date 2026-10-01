using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ApiSiteAnalyzer.Collect;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;

namespace ApiSiteAnalyzer.Web;

/// <summary>
/// 工作状态监视器——按「最近一次提交」距现在多久，给出 工作中 / 疑似停工 / 已停工 三态。
/// 为什么归服务端：判定要跨页面存活（关掉页面照样算），且满 180 秒的**反查**要真的去站点取数。
/// 反查**无视自动采集条件**——不看开关 / 不等间隔 / 不管该站已降级或停采，逐站强制拉一轮；
/// 全部站点都没有新记录才判「已停工」（有一站查不动就如实说未确认，不冒充已停工）。
/// </summary>
public sealed class WorkWatch
{
    /// <summary>心跳间隔（毫秒）。</summary>
    private const int TickMs = 1000;

    /// <summary>「工作中」阈值（秒）——最近一次提交在此之内。</summary>
    private const int WorkSeconds = 30;

    /// <summary>反查触发点（秒）——满这么多秒没新提交就触发一次全站反查；也是进度条的满格点。</summary>
    private const int CheckSeconds = 180;

    /// <summary>等采集串行槽位空闲的上限（毫秒）——另一采集在跑时等它，不与它并发驱动同一浏览器会话。</summary>
    private const int BusyWaitMs = 60000;

    /// <summary>轮询采集串行槽位的间隔（毫秒）。</summary>
    private const int BusyPollMs = 500;

    /// <summary>站点会话。</summary>
    private readonly SiteSession _session;

    /// <summary>站点清单。</summary>
    private readonly List<IApiSite> _sites;

    /// <summary>站点键清单（读库内最新提交时刻用）。</summary>
    private readonly List<string> _siteIds = new List<string>();

    /// <summary>库路径。</summary>
    private readonly string _dbPath;

    /// <summary>面板运行态（采集串行槽位与对外快照都在这里）。</summary>
    private readonly PanelState _state;

    /// <summary>状态读写锁（心跳与反查任务两个线程都改下面这几个字段）。</summary>
    private readonly object _gate = new object();

    /// <summary>库内最新提交时刻（unix 秒；0 = 还没读到记录）。</summary>
    private long _lastSubmitAt;

    /// <summary>上一次反查的发起时刻（unix 秒；0 = 还没查过）。</summary>
    private long _lastCheckAt;

    /// <summary>反查是否正在跑（防重入——一个空闲窗口只发一次）。</summary>
    private bool _checking;

    /// <summary>是否已确认停工（反查跑完且全站无新记录）。</summary>
    private bool _confirmed;

    /// <summary>反查结论 / 说明（空 = 无）。</summary>
    private string _detail = "";

    /// <summary>构造工作状态监视器。</summary>
    /// <param name="session">站点会话。</param>
    /// <param name="sites">站点清单。</param>
    /// <param name="dbPath">库路径。</param>
    /// <param name="state">面板运行态。</param>
    public WorkWatch(SiteSession session, List<IApiSite> sites, string dbPath, PanelState state)
    {
        _session = session;
        _sites = sites;
        _dbPath = dbPath;
        _state = state;

        // [段1] 站点键预取一次——心跳每秒读一次库内最新提交时刻，不必每次重新枚举适配器
        foreach (IApiSite site in sites)
        {
            _siteIds.Add(site.Id);
        }
    }

    /// <summary>主循环——每秒对一次表：库内最新提交时刻变了就回「工作中」；满 180 秒触发一次全站反查。</summary>
    /// <param name="ct">取消令牌（面板停机即退出）。</param>
    /// <returns>异步任务。</returns>
    public async Task RunAsync(CancellationToken ct)
    {
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

            Tick();
        }
    }

    /// <summary>一次心跳——读最新提交时刻、分档、到点就发起反查，并把快照公布给面板。</summary>
    private void Tick()
    {
        long latest = ReadLatest();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool startCheck = false;
        WorkSnapshot snapshot;

        lock (_gate)
        {
            // [段1] 有新提交——回工作中；反查闸门复位（下一次空闲满 180 秒重新查）
            if (latest > _lastSubmitAt)
            {
                _lastSubmitAt = latest;
                _confirmed = false;
                _detail = "";
            }

            double elapsed = _lastSubmitAt > 0 ? now - _lastSubmitAt : 0;
            if (elapsed < 0)
            {
                // 站点时钟比本机快——按 0 计，不把负值画成进度条
                elapsed = 0;
            }

            // [段2] 满 180 秒——触发一次反查（每次空闲窗口只发一次；查过仍无新增则每 180 秒复查一次）
            if (_lastSubmitAt > 0 && elapsed >= CheckSeconds && !_checking && now - _lastCheckAt >= CheckSeconds)
            {
                _checking = true;
                _lastCheckAt = now;
                startCheck = true;
            }

            snapshot = Build(elapsed);
        }

        _state.SetWork(snapshot);

        if (startCheck)
        {
            _ = Task.Run(ReverseCheckAsync);
        }
    }

    /// <summary>按距最近一次提交的秒数分档，组装对外快照。</summary>
    /// <param name="elapsed">距最近一次提交的秒数（已夹到非负）。</param>
    /// <returns>快照。</returns>
    private WorkSnapshot Build(double elapsed)
    {
        var work = new WorkSnapshot
        {
            LastSubmitAt = _lastSubmitAt,
            ElapsedSeconds = elapsed,
            WindowSeconds = CheckSeconds,
            Checking = _checking,
            Detail = _detail,
        };

        if (_lastSubmitAt <= 0)
        {
            // 库内还没有记录——如实说「无记录」，不冒充工作中
            work.State = "none";
            work.Label = "无记录";
            work.ElapsedSeconds = 0;
            return work;
        }

        if (elapsed < WorkSeconds)
        {
            work.State = "working";
            work.Label = "工作中";
            return work;
        }

        if (elapsed < CheckSeconds)
        {
            work.State = "suspect";
            work.Label = "疑似停工";
            return work;
        }

        if (_confirmed)
        {
            work.State = "stopped";
            work.Label = "已停工";
            return work;
        }

        // 满 180 秒但还没得到反查结论——先按疑似显示，结论到了才转「已停工」
        work.State = "suspect";
        work.Label = _checking ? "疑似停工 · 反查中" : "疑似停工";
        return work;
    }

    /// <summary>读库内最新一条记录的提交时刻（跨站取最大）——读不动时按「没读到」处理，不打断心跳。</summary>
    /// <returns>unix 秒；无记录或读失败返回 0。</returns>
    private long ReadLatest()
    {
        try
        {
            using var db = new Db(_dbPath);
            return db.LatestCreatedAt(_siteIds);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 工作状态：读库失败（" + ex.Message + "）——本轮沿用上次时刻");
            return 0;
        }
    }

    /// <summary>
    /// 全站反查——**无视自动采集条件**（开关关着 / 间隔没到 / 该站已降级或停采，一律照查），逐站强制拉一轮。
    /// 结论：全站都没有新记录 → 确认「已停工」；任何一站查不动 → 如实说未确认（不冒充已停工）。
    /// </summary>
    private async Task ReverseCheckAsync()
    {
        long before = ReadLatest();
        bool anyNew = false;
        bool allChecked = true;
        var failures = new List<string>();

        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 反查开始：距最近一次提交已满 " + CheckSeconds +
            " 秒——无视自动采集条件，逐站强制拉一轮");

        foreach (IApiSite site in _sites)
        {
            if (!await WaitIdleAsync(site).ConfigureAwait(false))
            {
                allChecked = false;
                failures.Add(site.DisplayName + "：另一采集仍在进行中");
                continue;
            }

            try
            {
                FetchSummary summary = await ServeRunner.FetchSiteAsync(_session, site, _dbPath, _state, true).ConfigureAwait(false);
                if (!summary.Ok)
                {
                    allChecked = false;
                    failures.Add(site.DisplayName + "：" + (summary.NotLoggedIn ? "未登录" : summary.Error));
                }
                else if (summary.Added > 0)
                {
                    anyNew = true;
                }
            }
            catch (Exception ex)
            {
                allChecked = false;
                failures.Add(site.DisplayName + "：" + ex.Message);
                _state.Fail("反查失败：" + ex.Message);
            }

            // 单站回执不作准——库内实况才是「有没有新提交」的判据
            if (ReadLatest() > before)
            {
                anyNew = true;
            }
        }

        string conclusion;
        if (anyNew)
        {
            conclusion = "反查取到新记录——回到工作中";
        }
        else if (allChecked)
        {
            conclusion = "反查完成：全站无新记录（" + DateTime.Now.ToString("HH:mm:ss") + "）";
        }
        else
        {
            conclusion = "反查未确认：" + string.Join("；", failures);
        }

        lock (_gate)
        {
            _checking = false;
            _confirmed = !anyNew && allChecked;
            _detail = conclusion;
        }

        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 反查结束：" + conclusion);
    }

    /// <summary>等采集串行槽位空闲并抢占——超时返回 false（不与正在跑的采集并发驱动同一浏览器会话）。</summary>
    /// <param name="site">站点适配器。</param>
    /// <returns>抢到返回 true。</returns>
    private async Task<bool> WaitIdleAsync(IApiSite site)
    {
        int waited = 0;
        while (!_state.TryBegin(site.Id, site.DisplayName + "（反查）"))
        {
            if (waited >= BusyWaitMs)
            {
                return false;
            }

            await Task.Delay(BusyPollMs).ConfigureAwait(false);
            waited = waited + BusyPollMs;
        }

        return true;
    }
}
