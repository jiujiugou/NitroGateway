# tools/ai-metrics —— AI 工程产出率月度分析

读取 Codex 会话库 / 会话 JSONL / Git 日志，统计一个月的 AI 工程投入与产出，
用于计算 AI 工程产出率、发现流程优化抓手。只读数据，不改写任何源。

## 用法

```powershell
# 默认统计"上一个月"，输出到控制台
python tools\ai-metrics\ai_productivity.py

# 指定月份并输出 JSON
python tools\ai-metrics\ai_productivity.py --start 2026-08-01 --end 2026-09-01 --out-dir .

# 换机器/仓库时指定路径
python tools\ai-metrics\ai_productivity.py --repo D:\Code\NitroGateway --state-db C:\Users\<user>\.codex\state_5.sqlite
```

## 指标口径（重要，请先读）

| 指标 | 口径 |
| --- | --- |
| Agent任务数 | 当月、用户发起的、有 token 计费的工程会话线程（排除 auto-review/闲聊会话） |
| 完成任务数 | 会话 JSONL 中出现 `task_complete` 事件的线程 |
| 一次验收通过数 | 完成且全程无 `turn_aborted`（中断）的线程 |
| 返工数 | 出现 ≥1 次 `turn_aborted` 的线程（含用户中途打断重来、失败重试） |
| Commit / 新增代码 / 新增测试 | `git log --numstat`，代码=src/web/deploy，测试=tests/ 下，文档=docs/notes/*.md |
| Bug | 提交信息含 fix/bug/修复/修正/回滚 等关键词的提交数 |
| AI成本 | token 数 × 模型单价估算（见下），真实值以 DeepSeek 账单为准 |

## 成本估算假设

`threads.tokens_used` 只记录输入+输出合计 token，无法区分输入/输出与缓存命中率，
故成本为估算：默认按 DeepSeek V4 低谷时段价 + 90% 输入/10% 输出。可在
`--prices '{"模型":{"in":..,"out":..,"cache":..}}'` 填入真实单价后重算。
