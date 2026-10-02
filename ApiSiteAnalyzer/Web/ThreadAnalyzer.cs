using System;
using System.Collections.Generic;
using ApiSiteAnalyzer.Sites;
using ApiSiteAnalyzer.Store;

namespace ApiSiteAnalyzer.Web;

/// <summary>线程链上的一轮——一次请求（一条记录）在链里的位置与判定结果。</summary>
public sealed class ThreadStep
{
    /// <summary>链内序号（0 起）。</summary>
    public int Index { get; set; }

    /// <summary>发生时刻（Unix 秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>输入 token。</summary>
    public long PromptTokens { get; set; }

    /// <summary>输出 token。</summary>
    public long CompletionTokens { get; set; }

    /// <summary>请求 ID。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>模型名。</summary>
    public string ModelName { get; set; } = "";

    /// <summary>所属站点显示名（总览视图用）。</summary>
    public string SiteName { get; set; } = "";

    /// <summary>本轮输入 + 输出——下一轮的期望输入值。</summary>
    public long ExpectNext
    {
        get
        {
            return PromptTokens + CompletionTokens;
        }
    }

    /// <summary>本轮是否并发轮（容差内后继多于一个 · 或与另一条链的输入重合）。</summary>
    public bool Concurrent { get; set; }

    /// <summary>并发原因（并发轮为必填——失败可见，不静默标红）。</summary>
    public string ConcurrentReason { get; set; } = "";
}

/// <summary>一条线程链——按「上一轮输入 + 输出 == 下一轮输入（容差 5%）」串起来的一组请求。</summary>
public sealed class ThreadChain
{
    /// <summary>链序号（0 起，按链首时刻倒序）。</summary>
    public int Index { get; set; }

    /// <summary>链上各轮（按时刻升序）。</summary>
    public List<ThreadStep> Steps { get; set; } = new List<ThreadStep>();

    /// <summary>链是否含并发轮。</summary>
    public bool HasConcurrent { get; set; }
}

/// <summary>线程分析结果——一份样本的链清单与并发统计。</summary>
public sealed class ThreadAnalysisResult
{
    /// <summary>样本条数（实际取到的记录数）。</summary>
    public int SampleCount { get; set; }

    /// <summary>样本请求上限（最近 N 条）。</summary>
    public int SampleLimit { get; set; }

    /// <summary>串出的链数。</summary>
    public int ChainCount { get; set; }

    /// <summary>并发轮计数。</summary>
    public int ConcurrentStepCount { get; set; }

    /// <summary>含并发的链数。</summary>
    public int ConcurrentChainCount { get; set; }

    /// <summary>链清单（按链首时刻倒序）。</summary>
    public List<ThreadChain> Chains { get; set; } = new List<ThreadChain>();
}

/// <summary>
/// 线程（会话链）分析器——把最近一批请求按「上一轮输入 + 输出 == 下一轮输入」串成链，并标出并发。
/// **只分析、不落库**——标记是运行时的结论，不进 SQLite（需求定案：不落库）。
/// </summary>
public static class ThreadAnalyzer
{
    /// <summary>样本条数——取最近这么多条记录做串链。</summary>
    public const int SampleLimit = 200;

    /// <summary>容差比例——两值相对误差不超过它即视为相等（0.05 = 5%）。</summary>
    public const double Tolerance = 0.05;

    /// <summary>
    /// 分析一份样本（**按时刻升序**传入——调用方负责排序）。
    /// </summary>
    /// <param name="records">样本记录（按时刻升序）。</param>
    /// <param name="siteNameOf">取站点显示名的函数（总览视图里每行站点可能不同）。</param>
    /// <returns>链清单与并发统计。</returns>
    public static ThreadAnalysisResult Analyze(List<UsageRecord> records, Func<string, string> siteNameOf)
    {
        var result = new ThreadAnalysisResult();
        result.SampleLimit = SampleLimit;
        result.SampleCount = records.Count;

        // [段1] 逐条建轮次——样本按时刻升序排列，下标即时间序
        var steps = new List<ThreadStep>();
        foreach (UsageRecord record in records)
        {
            steps.Add(new ThreadStep
            {
                Index = steps.Count,
                CreatedAt = record.CreatedAt,
                PromptTokens = record.PromptTokens,
                CompletionTokens = record.CompletionTokens,
                RequestId = record.RequestId,
                ModelName = record.ModelName,
                SiteName = siteNameOf(record.SiteId),
            });
        }

        // [段2] 连边——第 i 轮的期望值 = 输入 + 输出；在它之后找容差内输入的最早一轮作唯一后继
        var nextOf = new int[steps.Count];
        var forkOf = new List<int>[steps.Count];
        for (int i = 0; i < steps.Count; i = i + 1)
        {
            nextOf[i] = -1;
            forkOf[i] = new List<int>();
        }

        for (int i = 0; i < steps.Count; i = i + 1)
        {
            long expect = steps[i].ExpectNext;

            // [段2a] 全量候选——只用来算「第一跳」，不直接当并发判据
            var all = new List<int>();
            for (int j = i + 1; j < steps.Count; j = j + 1)
            {
                if (WithinTolerance(expect, steps[j].PromptTokens))
                {
                    all.Add(j);
                }
            }

            if (all.Count == 0)
            {
                continue;
            }

            nextOf[i] = all[0];

            // [段2b] 下一跳窗口——窗宽 = 到第一候选的时长（步长自适应，不引入外部阈值）：
            //       下一轮本该在这个时刻附近到达；落在窗外的同值记录属于别处（另一条链在很后的时刻
            //       又用到同一量级的输入），不算「同时来了多个去向」。用例：顺序跑完的两条链不该误报
            long gap = steps[all[0]].CreatedAt - steps[i].CreatedAt;
            long limit = steps[all[0]].CreatedAt + gap;
            foreach (int j in all)
            {
                if (steps[j].CreatedAt <= limit)
                {
                    forkOf[i].Add(j);
                }
            }
        }

        // [段3] 链首判定——没有任何一轮指向它，它即为一条链的起点
        var referenced = new bool[steps.Count];
        for (int i = 0; i < steps.Count; i = i + 1)
        {
            if (nextOf[i] >= 0)
            {
                referenced[nextOf[i]] = true;
            }
        }

        // [段4] 串链——从每个链首沿 nextOf 走到尾；已被前一条链走过的轮不再另起链（防分叉处重复成链）
        var taken = new bool[steps.Count];
        var chains = new List<ThreadChain>();
        for (int i = 0; i < steps.Count; i = i + 1)
        {
            if (referenced[i] || taken[i])
            {
                continue;
            }

            var chain = new ThreadChain();
            chain.Index = chains.Count;
            int at = i;
            while (at >= 0 && !taken[at])
            {
                taken[at] = true;
                chain.Steps.Add(steps[at]);
                at = nextOf[at];
            }
            chains.Add(chain);
        }

        if (chains.Count == 0)
        {
            return result;
        }

        // [段5] 并发标记一：分叉——同一跳的窗口内来了多个容差内后继，说明这一跳之后同时存在多条去向。
        //       分叉点**两侧**都标：轮首标「出度 N」，窗口内各候选标「分叉候选」——
        //       只标轮首会漏掉「这一轮是另一条链的候选后继」这个事实（它同样说明此处存在并行去向）
        for (int i = 0; i < steps.Count; i = i + 1)
        {
            if (forkOf[i].Count < 2)
            {
                continue;
            }

            steps[i].Concurrent = true;
            steps[i].ConcurrentReason = "出度 " + forkOf[i].Count + "（容差内后继多于一个）";
            foreach (int hit in forkOf[i])
            {
                if (steps[hit].Concurrent)
                {
                    continue;
                }

                steps[hit].Concurrent = true;
                steps[hit].ConcurrentReason = "分叉候选（第 " + (i + 1) + " 轮的容差内后继有 " +
                    forkOf[i].Count + " 个）";
            }
        }

        // [段6] 并发标记二：交叠——两条链**在时间上交错**（各自的时刻区间互相插入对方内部，
        //       即同一时间窗里两条链都活着），且有轮对的输入在容差内重合（70 / 71 型）。
        //       **两条判据都要成立**——只看输入值会把「同量级但一前一后跑完」的链全误标
        var chainOf = new int[steps.Count];
        for (int i = 0; i < steps.Count; i = i + 1)
        {
            chainOf[i] = -1;
        }
        foreach (ThreadChain chain in chains)
        {
            foreach (ThreadStep step in chain.Steps)
            {
                chainOf[step.Index] = chain.Index;
            }
        }

        var spanFrom = new long[chains.Count];
        var spanTo = new long[chains.Count];
        for (int c = 0; c < chains.Count; c = c + 1)
        {
            spanFrom[c] = long.MaxValue;
            spanTo[c] = long.MinValue;
            foreach (ThreadStep step in chains[c].Steps)
            {
                if (step.CreatedAt < spanFrom[c])
                {
                    spanFrom[c] = step.CreatedAt;
                }

                if (step.CreatedAt > spanTo[c])
                {
                    spanTo[c] = step.CreatedAt;
                }
            }
        }

        for (int x = 0; x < chains.Count; x = x + 1)
        {
            for (int y = x + 1; y < chains.Count; y = y + 1)
            {
                // 区间互不插入 = 两条链一前一后跑完，不构成交叠
                if (spanFrom[x] > spanTo[y] || spanFrom[y] > spanTo[x])
                {
                    continue;
                }

                foreach (ThreadStep left in chains[x].Steps)
                {
                    foreach (ThreadStep right in chains[y].Steps)
                    {
                        if (!WithinTolerance(left.PromptTokens, right.PromptTokens))
                        {
                            continue;
                        }

                        if (!left.Concurrent)
                        {
                            left.Concurrent = true;
                            left.ConcurrentReason = "与链 #" + (y + 1) + " 交错且输入重合（" + left.PromptTokens
                                + " / " + right.PromptTokens + "）";
                        }

                        if (!right.Concurrent)
                        {
                            right.Concurrent = true;
                            right.ConcurrentReason = "与链 #" + (x + 1) + " 交错且输入重合（" + right.PromptTokens
                                + " / " + left.PromptTokens + "）";
                        }
                    }
                }
            }
        }

        // [段7] 收敛统计——链标记由轮标记推出（单点真相：链是否并发只看它的轮）
        foreach (ThreadChain chain in chains)
        {
            foreach (ThreadStep step in chain.Steps)
            {
                if (step.Concurrent)
                {
                    chain.HasConcurrent = true;
                }
            }
        }

        result.Chains = chains;
        result.ChainCount = chains.Count;
        foreach (ThreadStep step in steps)
        {
            if (step.Concurrent)
            {
                result.ConcurrentStepCount = result.ConcurrentStepCount + 1;
            }
        }
        foreach (ThreadChain chain in chains)
        {
            if (chain.HasConcurrent)
            {
                result.ConcurrentChainCount = result.ConcurrentChainCount + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// 两个值是否在容差内相等——相对误差以较大者为分母（对称：a 比 b 与 b 比 a 同结论）。
    /// </summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>在容差内返回 true。</returns>
    public static bool WithinTolerance(long left, long right)
    {
        if (left == right)
        {
            return true;
        }

        long bigger = Math.Max(Math.Abs(left), Math.Abs(right));
        if (bigger == 0)
        {
            return true;
        }

        return Math.Abs(left - right) <= bigger * Tolerance;
    }
}
