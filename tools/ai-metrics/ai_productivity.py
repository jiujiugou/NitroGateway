#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AI 工程产出率月度分析工具（NitroGateway 专用）

输入源（只读，不改写任何数据）：
  1. Codex 会话状态库   C:/Users/<user>/.codex/state_5.sqlite      -> 线程、token、模型、任务标题
  2. Codex 会话 JSONL   C:/Users/<user>/.codex/sessions/...        -> 完成/中断/轮次事件
  3. Git 仓库日志        git log --numstat                          -> 提交、代码/测试/文档行数、Bug

用法：
  python ai_productivity.py                              # 默认统计上一个月
  python ai_productivity.py --start 2026-08-01 --end 2026-09-01
  python ai_productivity.py --repo D:\\Code\\NitroGateway --cwd-filter NitroGateway
  python ai_productivity.py --out-dir .                   # 输出 report.md / report.json
  python ai_productivity.py --prices '{"deepseek-v4-flash":{"in":1.5,"out":4.5,"cache":0.05}}'

成本估算说明：
  threads.tokens_used 只记录"输入+输出"合计 token，无法区分输入/输出与缓存命中率，
  因此成本为估算值。脚本默认使用 DeepSeek V4 2026-08-17 起生效的"低谷时段"官方价
  （输入未命中/输出，单位 元/百万 tokens），并假设 Agent 负载约 90% 输入 + 10% 输出。
  真实成本请以 DeepSeek 控制台账单为准，可在 --prices 中填入实际价格后重算。
"""

import argparse
import collections
import datetime
import json
import os
import re
import sqlite3
import subprocess
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

DEFAULT_CODEX_HOME = os.path.expanduser(r"~\.codex")

# 默认价格表（元 / 百万 tokens；输入为缓存未命中价；低谷时段，2026-08-17 起生效）
# 输出=输出价；cache=缓存命中输入价。Agent 负载约 90% 输入 + 10% 输出。
DEFAULT_PRICES = {
    "deepseek-v4-flash": {"in": 1.5, "out": 4.5, "cache": 0.05},
    "deepseek-v4-pro": {"in": 4.5, "out": 13.5, "cache": 0.15},
    "gpt-5.6-luna": {"in": 2.0, "out": 8.0, "cache": 0.2},
}
INPUT_RATIO = 0.9  # 假设输入 token 占比


def ts_to_str(ts):
    return datetime.datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M") if ts else ""


def clean_path(p):
    return p.replace("\\\\?\\", "") if p else p


def connect_ro(db_path):
    uri = "file:" + db_path.replace("\\", "/") + "?mode=ro"
    return sqlite3.connect(uri, uri=True)


def last_month():
    today = datetime.date.today()
    first = today.replace(day=1)
    prev = first - datetime.timedelta(days=1)
    return prev.replace(day=1), first


def parse_rollout(path):
    """从单条会话 JSONL 中提取完成/中断/轮次事件计数，以及最后一条 token_count 的累计用量。"""
    rec = {"task_complete": 0, "task_started": 0, "turn_aborted": 0, "usage": None}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    j = json.loads(line)
                except Exception:
                    continue
                typ = j.get("type")
                if typ == "event_msg":
                    pt = (j.get("payload") or {}).get("type")
                    if pt == "task_complete":
                        rec["task_complete"] += 1
                    elif pt == "task_started":
                        rec["task_started"] += 1
                    elif pt == "turn_aborted":
                        rec["turn_aborted"] += 1
                    elif pt == "token_count":
                        info = (j.get("payload") or {}).get("info") or {}
                        tu = info.get("total_token_usage")
                        if tu and tu.get("total_tokens"):
                            rec["usage"] = tu  # 累计快照，取最后一条即全程用量
    except FileNotFoundError:
        rec["missing"] = True
    return rec


def load_threads(state_db, start_ts, end_ts, cwd_filter):
    con = connect_ro(state_db)
    con.row_factory = sqlite3.Row
    cur = con.cursor()
    params = [start_ts, end_ts]
    like = ""
    if cwd_filter:
        like = " AND cwd LIKE ?"
        params.append("%" + cwd_filter + "%")
    rows = cur.execute(
        "SELECT id, rollout_path, created_at, tokens_used, model, title, first_user_message,"
        " thread_source, source, cwd FROM threads"
        " WHERE created_at >= ? AND created_at < ?" + like,
        params,
    ).fetchall()
    con.close()
    out = []
    for r in rows:
        rec = {
            "id": r["id"],
            "rollout_path": r["rollout_path"],
            "created_at": r["created_at"],
            "tokens": r["tokens_used"] or 0,
            "model": r["model"],
            "title": (r["title"] or r["first_user_message"] or "")[:200],
            "thread_source": r["thread_source"],
            "cwd": r["cwd"],
        }
        out.append(rec)
    return out


def git_metrics(repo, start, end):
    """提交数、类型分布、Bug/修复提交、按类别统计增删行。"""
    def run(*args):
        return subprocess.run(
            list(args), cwd=repo, capture_output=True, text=True, encoding="utf-8", errors="replace"
        ).stdout

    log = run(
        "git", "log", "--since=" + start, "--until=" + end,
        "--pretty=%H|%ad|%an|%s", "--date=short", "--no-merges",
    ).strip().splitlines()
    commits = []
    for line in log:
        parts = line.split("|", 3)
        if len(parts) == 4:
            commits.append({"hash": parts[0], "date": parts[1], "author": parts[2], "subject": parts[3]})

    types = collections.Counter()
    bug_re = re.compile(r"^(fix|hotfix|revert)|bug|修复|修正|回滚|回归|缺陷", re.I)
    bugs, strict_fix = [], 0
    for c in commits:
        m = re.match(r"^([a-z]+)(\([^)]*\))?:", c["subject"])
        types[m.group(1) if m else "other"] += 1
        if bug_re.search(c["subject"]):
            bugs.append(c)
        if c["subject"].startswith("fix"):
            strict_fix += 1

    def cat_of(path):
        pl = path.lower()
        base = os.path.basename(pl)
        if re.search(r"(^|/)tests?/", pl) or re.search(r"(^|_|\.)(test|tests|unittests?)(\.|_|$)", base) or ".tests." in pl:
            return "test"
        if pl.startswith(("docs/", "notes/")) or pl.endswith((".md", ".mdx")):
            return "docs"
        if pl.startswith(("src/", "web/", "deploy/", ".github/")) or pl.endswith(
            (".cs", ".ts", ".vue", ".js", ".csproj", ".slnx", ".json", ".ps1", ".yml", ".yaml", ".sql", ".sh", ".toml")
        ):
            return "code"
        return "other"

    numstat = run("git", "log", "--since=" + start, "--until=" + end, "--pretty=tformat:", "--numstat", "--no-merges")
    lines = {"code": [0, 0], "test": [0, 0], "docs": [0, 0], "other": [0, 0]}
    files = collections.Counter()
    test_files = []
    for ln in numstat.splitlines():
        parts = ln.split("\t")
        if len(parts) != 3 or parts[0] == "-":
            continue
        cat = cat_of(parts[2])
        lines[cat][0] += int(parts[0])
        lines[cat][1] += int(parts[1])
        files[cat] += 1
        if cat == "test":
            test_files.append(parts[2])
    return {
        "commits": commits,
        "types": dict(types),
        "bugs": bugs,
        "strict_fix": strict_fix,
        "lines": lines,
        "files": dict(files),
        "test_files": test_files,
    }


def estimate_cost(threads, prices):
    """按模型 + 真实 token 构成（缓存未命中/命中/输出）估算成本（元）。
    优先使用会话 JSONL 中 token_count 事件提供的精确输入(缓存命中/未命中)/输出拆分；
    缺失时回退到 INPUT_RATIO 假设。"""
    per_model = collections.defaultdict(lambda: {"miss": 0, "cached": 0, "out": 0, "n": 0})
    total = 0.0
    detail = {}
    for t in threads:
        if t["tokens"] <= 0:
            continue
        u = t.get("usage")
        if u:
            m = t["model"]
            per_model[m]["miss"] += u.get("input_tokens", 0) - u.get("cached_input_tokens", 0)
            per_model[m]["cached"] += u.get("cached_input_tokens", 0)
            per_model[m]["out"] += u.get("output_tokens", 0)
            per_model[m]["n"] += 1
    for m, a in per_model.items():
        p = prices.get(m)
        if not p:
            detail[m] = None
            continue
        if a["n"]:
            c = a["miss"] / 1e6 * p["in"] + a["cached"] / 1e6 * p["cache"] + a["out"] / 1e6 * p["out"]
        else:
            tok = sum(t["tokens"] for t in threads if t["model"] == m)
            c = tok / 1e6 * (INPUT_RATIO * p["in"] + (1 - INPUT_RATIO) * p["out"])
        total += c
        detail[m] = c
    return total, detail, per_model


def build_report(args, start_ts, end_ts):
    threads = load_threads(args.state_db, start_ts, end_ts, args.cwd_filter)
    # 解析 rollout（仅对含 token 的用户工程任务，避免给无计费的 auto-review/闲聊线程开销）
    user_eng = [t for t in threads if t["tokens"] > 0 and t["thread_source"] == "user"]
    for t in user_eng:
        t.update(parse_rollout(clean_path(t["rollout_path"]) if t.get("rollout_path") else ""))

    subagent = [t for t in threads if t["thread_source"] == "subagent"]
    no_token = [t for t in threads if t["tokens"] <= 0 and t["thread_source"] != "subagent"]

    total_tokens = sum(t["tokens"] for t in user_eng)
    completed = [t for t in user_eng if t["task_complete"] > 0]
    clean = [t for t in completed if t["turn_aborted"] == 0]
    one_shot = [t for t in clean if t["task_started"] == 1]
    aborted = [t for t in user_eng if t["turn_aborted"] > 0]
    not_completed = [t for t in user_eng if t["task_complete"] == 0]
    heavy = [t for t in user_eng if t["task_started"] > 5]

    git = git_metrics(args.repo, args.start, args.end)
    cost, cost_detail, usage = estimate_cost(user_eng, args.prices)

    n = len(user_eng)
    code_add, test_add = git["lines"]["code"][0], git["lines"]["test"][0]
    code_test_add = code_add + test_add
    report = {
        "period": {"start": args.start, "end": args.end},
        "threads": {
            "total_sessions": len(threads),
            "user_eng_tasks": n,
            "subagent_reviews": len(subagent),
            "no_token_sessions": len(no_token),
            "completed": len(completed),
            "completed_rate": round(len(completed) / n * 100, 1) if n else 0,
            "first_pass_clean": len(clean),
            "first_pass_rate": round(len(clean) / n * 100, 1) if n else 0,
            "one_shot": len(one_shot),
            "one_shot_rate": round(len(one_shot) / n * 100, 1) if n else 0,
            "aborted_or_rework": len(aborted),
            "aborted_rate": round(len(aborted) / n * 100, 1) if n else 0,
            "not_completed": len(not_completed),
            "heavy_multi_turn": len(heavy),
        },
        "tokens": {
            "total": total_tokens,
            "per_task": round(total_tokens / n, 1) if n else 0,
            "by_model": {m: sum(t["tokens"] for t in user_eng if t["model"] == m) for m in
                         sorted(set(t["model"] for t in user_eng))},
            "on_clean": sum(t["tokens"] for t in clean),
            "on_aborted": sum(t["tokens"] for t in aborted),
            "on_heavy": sum(t["tokens"] for t in heavy),
        },
        "cost": {"estimate_cny": round(cost, 2), "detail": cost_detail, "input_ratio": INPUT_RATIO},
        "usage": {
            "miss": sum(a["miss"] for a in usage.values()),
            "cached": sum(a["cached"] for a in usage.values()),
            "output": sum(a["out"] for a in usage.values()),
        },
        "git": {
            "commits": len(git["commits"]),
            "types": git["types"],
            "bugs": len(git["bugs"]),
            "strict_fix": git["strict_fix"],
            "lines": git["lines"],
            "files": git["files"],
            "test_files": len(git["test_files"]),
        },
        "productivity": {
            "code_lines_per_mtok": round(code_add / (total_tokens / 1e6), 1) if total_tokens else 0,
            "code_test_lines_per_mtok": round(code_test_add / (total_tokens / 1e6), 1) if total_tokens else 0,
            "test_to_code_ratio": round(test_add / code_add * 100, 1) if code_add else 0,
            "tokens_per_task": round(total_tokens / n, 1) if n else 0,
            "cost_per_task": round(cost / n, 2) if n and cost else 0,
            "code_lines_per_100cny": round(code_test_add / cost * 100, 1) if cost else 0,
        },
    }
    return report, threads, git


def fmt_lines(report):
    l = report["git"]["lines"]
    return l


def print_report(report):
    t, tk, c, g, p = report["threads"], report["tokens"], report["cost"], report["git"], report["productivity"]
    print("=" * 74)
    print(f"AI 工程产出率月度报告  {report['period']['start']} ~ {report['period']['end']}")
    print("=" * 74)
    print("\n[1] AI 投入")
    print(f"  Agent任务数(用户工程任务) : {t['user_eng_tasks']}   (另有自动评审 {t['subagent_reviews']}、无计费会话 {t['no_token_sessions']})")
    print(f"  Token(合计)              : {tk['total']:,}  ≈ {tk['total']/1e6:.1f} M   | 单任务 {p['tokens_per_task']/1e6:.2f} M")
    for m, v in tk["by_model"].items():
        print(f"      {m:22s}: {v:,} ({v/tk['total']*100:.1f}%)")
    u = report["usage"]
    u_tot = u["miss"] + u["cached"] + u["output"] or 1
    print(f"      Token构成(真实拆分)   : 缓存命中输入 {u['cached']:,} ({u['cached']/u_tot*100:.1f}%) | "
          f"未命中输入 {u['miss']:,} ({u['miss']/u_tot*100:.1f}%) | 输出 {u['output']:,} ({u['output']/u_tot*100:.1f}%)")
    print(f"  AI成本(估算, 元)          : ¥ {c['estimate_cny']:,.0f} (低谷价) ~ ¥ {c['estimate_cny']*2:,.0f} (高峰价)  以账单为准")
    print("\n[2] AI 产出")
    print(f"  完成任务数               : {t['completed']}/{t['user_eng_tasks']}  ({t['completed_rate']}%)")
    print(f"  一次验收通过数(无中断完成): {t['first_pass_clean']}/{t['user_eng_tasks']}  ({t['first_pass_rate']}%)  其中单轮一次通过 {t['one_shot']} ({t['one_shot_rate']}%)")
    print(f"  返工数(≥1次中断/重试)    : {t['aborted_or_rework']}  ({t['aborted_rate']}%)   未完成 {t['not_completed']}")
    print(f"  Commit                    : {g['commits']}  类型 {g['types']}")
    print(f"  新增代码(+/-)             : code {g['lines']['code'][0]:,}/{g['lines']['code'][1]:,} 行 ({g['files'].get('code',0)} 文件)")
    print(f"  新增测试(+/-)             : test {g['lines']['test'][0]:,}/{g['lines']['test'][1]:,} 行 ({g['test_files']} 测试文件)")
    print(f"  文档/其他新增             : docs {g['lines']['docs'][0]:,}  other {g['lines']['other'][0]:,}")
    print(f"  Bug(修复类提交)           : {g['bugs']}  (严格 fix: 前缀 {g['strict_fix']})")
    print("\n[3] 产出率")
    print(f"  任务完成率                : {t['completed_rate']}%")
    print(f"  一次性通过率              : {t['first_pass_rate']}%")
    print(f"  返工率                    : {t['aborted_rate']}%")
    print(f"  代码/百万token            : {p['code_lines_per_mtok']} 行/Mtok   (含测试 {p['code_test_lines_per_mtok']} 行/Mtok)")
    print(f"  测试:代码 比              : {p['test_to_code_ratio']}%")
    print(f"  每百元成本产出代码+测试   : {p['code_lines_per_100cny']} 行/百元")
    print(f"  Bug/任务 密度             : {g['bugs']}/{t['user_eng_tasks']} = {g['bugs']/t['user_eng_tasks']*100:.1f}%")
    print("\n[4] 浪费信号（优化抓手）")
    print(f"  中断线程消耗token占比     : {tk['on_aborted']/tk['total']*100:.1f}%  (按同比例约占计费输入/输出成本 {tk['on_aborted']/tk['total']*c['estimate_cny']:,.0f} 元)")
    print(f"  多轮(>5轮)任务token占比   : {tk['on_heavy']/tk['total']*100:.1f}%  (token 大头为缓存命中输入，成本影响小，主要影响时长与轮次)")
    print("=" * 74)


def main():
    ap = argparse.ArgumentParser(description="AI 工程产出率月度分析")
    ap.add_argument("--start", help="起始日期 YYYY-MM-DD（含）")
    ap.add_argument("--end", help="结束日期 YYYY-MM-DD（不含），默认用 start 所在月的下个月首日")
    ap.add_argument("--repo", default=r"D:\Code\NitroGateway")
    ap.add_argument("--state-db", default=os.path.join(DEFAULT_CODEX_HOME, "state_5.sqlite"))
    ap.add_argument("--cwd-filter", default="", help="按会话工作目录过滤（如 NitroGateway）")
    ap.add_argument("--prices", default=None, help='价格表 JSON，如 \'{"deepseek-v4-flash":{"in":1.5,"out":4.5}}\'')
    ap.add_argument("--out-dir", default=None, help="输出 report.md / report.json 到目录")
    args = ap.parse_args()

    if not args.start:
        s, e = last_month()
        args.start, args.end = s.isoformat(), e.isoformat()
    if not args.end:
        d = datetime.date.fromisoformat(args.start)
        args.end = (d.replace(day=1) + datetime.timedelta(days=32)).replace(day=1).isoformat()
    if args.prices:
        try:
            args.prices = json.loads(args.prices)
        except Exception as e:
            print("价格表 JSON 解析失败:", e)
            sys.exit(1)
    else:
        args.prices = DEFAULT_PRICES

    start_ts = datetime.datetime.fromisoformat(args.start).timestamp()
    end_ts = datetime.datetime.fromisoformat(args.end).timestamp()
    report, threads, git = build_report(args, start_ts, end_ts)
    print_report(report)

    if args.out_dir:
        os.makedirs(args.out_dir, exist_ok=True)
        with open(os.path.join(args.out_dir, "report.json"), "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=2)
        print("\n已输出:", os.path.join(args.out_dir, "report.json"))


if __name__ == "__main__":
    main()
