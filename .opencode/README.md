# 并发建模（轻量版）

把"AI 乱写并发"变成"先建模 → 照文档先写检测器 → 独立红队攻击 → 缺陷回流改模型"的小闭环。
只保留三样真正扛事的：**不变量表 / 可失败检测器 / 一次独立攻击**。审批、验收、校验器已删除。

## 先问：值得吗？

**默认不走这套流程。** 三个问题都答「是」才用：动的是共享生命周期状态吗？错了会静默难复现吗？爆炸半径大吗？
否则写几个能失败的并发测试 + 正常评审即可。

## 文件地图

| 路径 | 作用 |
|---|---|
| `skill/concurrency-modeling/SKILL.md` | 主技能：何时用、工作流、禁止清单 |
| `skill/concurrency-modeling/references/checklist.md` | ①–⑦ 逐条提问 |
| `skill/concurrency-modeling/references/template.md` | 文档骨架（契约 / 不变量 / 豁免） |
| `skill/concurrency-modeling/references/detectors.md` | 检测器 + 可执行负控 + 受控交错 |
| `skill/concurrency-modeling/references/antipatterns.md` | 反模式 + 非目标 |
| `agent/concurrency-modeler.md` | 建模（只写 `notes/Invariants/**`） |
| `agent/concurrency-adversary.md` | 红队（只写 `tests/**`） |
| `command/concurrency-*.md`、`command/并发*.md` | 手动入口（英/中） |

## 日常使用

```
/并发建模 <文件>    # 产出 notes/Invariants/<组件>.md（契约 / 不变量+检测器 / 豁免）
/并发实现 <组件>    # 先写检测器(红) → 实现(绿)
/并发攻击 <组件>    # 红队盲测 + 负控 → 分类回流
```

## 重启后

opencode 配置不热加载，改完命令/agent 需重启才会生效。会话持久化在 `~/.local/share/opencode`；即便恢复不了，`.opencode/` + `notes/Invariants/` 已包含继续工作所需的全部上下文。

## 待实测的不确定项

1. 中文命令名是否被 opencode 注册（不行→用英文名）。
2. subagent 是否继承父权限 + bash 能否绕过 `edit`。
