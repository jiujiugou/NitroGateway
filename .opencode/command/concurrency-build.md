---
description: "并发实现：照 notes/Invariants 文档，先写检测器(红)，再实现(绿)。"
agent: build
---

对 **$ARGUMENTS** 执行实现：

1. 读 `notes/Invariants/<组件>.md`，加载 `concurrency-modeling` 技能。
2. **先写检测器测试（在 seeded-fault 坏实现上必须变红）→ 再实现（转绿）**，运行测试并给**原始输出**。只给正例、没有可执行负控 = 未完成。
3. 实现若需要偏离模型 → 停止，回 `/并发建模` 改文档，**不得偷偷改语义或弱化检测器**。
