# -*- coding: utf-8 -*-
"""缓存命中持续跟进——壳台账只读扫描（默认近 24 小时）。

用法：python tools/cache_watch.py [小时数]
产物：tools/cache-watch/<yyyyMMdd-HHmm>.json / .txt（每次复跑留一份，供跨轮对比）

判据（2026-11-12 改动前基线，24h）：
  switches            552      目标 < 100
  low_credit_pct      44.0%    目标 < 15%
  stuck_convs         40+      目标 0（同一会话连续 >=2 次换号 = 绑定被钉住的死循环）
  新增归因 spread 应出现，且同号活跃会话数摊开
"""
import json
import os
import sys
import time
import collections

ROOT = r"D:\Mau\WorkSpace\ApiSiteAnalyzer\workbuddy-manager\work\shell\bin\Debug\net8.0-windows\data\run\usage"
OUT_DIR = r"D:\Mau\WorkSpace\ApiSiteAnalyzer\tools\cache-watch"
HOURS = int(sys.argv[1]) if len(sys.argv) > 1 else 24
now = time.time() * 1000
cut = now - HOURS * 3600 * 1000

days = sorted(set(time.strftime("%Y-%m-%d", time.localtime((cut + i * 86400000) / 1000)) for i in (0, 1)))
rows = []
for d in days:
    p = os.path.join(ROOT, "usage-%s.jsonl" % d)
    if not os.path.exists(p):
        continue
    with open(p, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except Exception:
                continue
            if r.get("at", 0) >= cut:
                rows.append(r)
rows.sort(key=lambda r: r["at"])


def rate(r):
    p = r.get("promptTokens") or 0
    return None if p <= 0 else (r.get("cacheTokens") or 0) / p


def ts(ms):
    return time.strftime("%m-%d %H:%M", time.localtime(ms / 1000))


valid = [r for r in rows if r.get("ok") and (r.get("promptTokens") or 0) > 0]
low = [r for r in valid if rate(r) is not None and rate(r) < 0.97]
credit_all = sum(r.get("credit") or 0 for r in valid)
credit_low = sum(r.get("credit") or 0 for r in low)

by_conv = collections.defaultdict(list)
for r in valid:
    by_conv[r.get("convId") or r.get("conversationId") or ""].append(r)
for lst in by_conv.values():
    lst.sort(key=lambda r: r["at"])

switches = []
stuck = 0
for cid, lst in by_conv.items():
    streak = 0
    conv_max = 0
    for i in range(1, len(lst)):
        if lst[i].get("uid") != lst[i - 1].get("uid"):
            switches.append((cid, lst[i - 1], lst[i]))
            streak += 1
            conv_max = max(conv_max, streak)
        else:
            streak = 0
    if conv_max >= 2:
        stuck += 1

sw_rate = [(r.get("cacheTokens") or 0) / r["promptTokens"] for _, _, r in switches]
reasons = collections.Counter("%s/%s" % (r.get("pickReason") or "-", r.get("prefStatus") or "-") for _, _, r in switches)

hourly = collections.OrderedDict()
for r in valid:
    k = time.strftime("%m-%d %H", time.localtime(r["at"] / 1000))
    b = hourly.setdefault(k, {"n": 0, "low": 0, "sw": 0})
    b["n"] += 1
    if rate(r) < 0.97:
        b["low"] += 1
for _, _, r in switches:
    k = time.strftime("%m-%d %H", time.localtime(r["at"] / 1000))
    hourly.setdefault(k, {"n": 0, "low": 0, "sw": 0})["sw"] += 1

summary = {
    "windowHours": HOURS,
    "windowFrom": ts(cut),
    "windowTo": ts(now),
    "requests": len(valid),
    "failed": len(rows) - len(valid),
    "cacheHitPctWeighted": round(100.0 * sum(r.get("cacheTokens") or 0 for r in valid) / sum(r["promptTokens"] for r in valid), 2) if valid else 0,
    "lowCount": len(low),
    "lowCreditPct": round(100.0 * credit_low / credit_all, 1) if credit_all else 0,
    "creditAll": round(credit_all, 1),
    "switches": len(switches),
    "switchFirstAvgPct": round(100.0 * sum(sw_rate) / len(sw_rate), 1) if sw_rate else 0,
    "stuckConvs": stuck,
    "switchByReason": dict(reasons.most_common(12)),
    "hourly": hourly,
    "verdict": {
        "switches_ok": len(switches) < 100,
        "lowCredit_ok": (100.0 * credit_low / credit_all if credit_all else 0) < 15.0,
        "stuck_ok": stuck == 0,
    },
}

os.makedirs(OUT_DIR, exist_ok=True)
stamp = time.strftime("%Y%m%d-%H%M", time.localtime(now / 1000))
with open(os.path.join(OUT_DIR, "cache-%s.json" % stamp), "w", encoding="utf-8") as f:
    json.dump(summary, f, ensure_ascii=False, indent=1)

lines = [
    "窗口 %s -> %s（%dh）" % (summary["windowFrom"], summary["windowTo"], HOURS),
    "请求 %d · 失败 %d · 加权命中率 %.2f%%" % (summary["requests"], summary["failed"], summary["cacheHitPctWeighted"]),
    "低命中 %d 条 · credit 占比 %.1f%%（%.1f / %.1f）" % (
        summary["lowCount"], summary["lowCreditPct"], credit_low, credit_all),
    "换号 %d 次 · 换号首条均值 %.1f%% · 卡死会话 %d" % (len(switches), summary["switchFirstAvgPct"], stuck),
    "换号归因：" + ("、".join("%s=%d" % (k, v) for k, v in reasons.most_common(8)) or "无"),
    "判据：换号<100 %s · 低命中credit<15%% %s · 卡死0 %s" % (
        "PASS" if summary["verdict"]["switches_ok"] else "FAIL",
        "PASS" if summary["verdict"]["lowCredit_ok"] else "FAIL",
        "PASS" if summary["verdict"]["stuck_ok"] else "FAIL"),
    "",
    "按小时（n / low / switch）：",
]
for k, v in hourly.items():
    lines.append("  %s  %5d / %4d / %3d" % (k, v["n"], v["low"], v["sw"]))
with open(os.path.join(OUT_DIR, "cache-%s.txt" % stamp), "w", encoding="utf-8") as f:
    f.write("\n".join(lines))

print("CACHE-WATCH -> %s" % os.path.join(OUT_DIR, "cache-%s.txt" % stamp))
