# ADR Rules

1. Check
遇到可能影响架构、核心依赖、模块边界、数据模型、协议、并发、存储或其他长期技术约束的问题时，先检查 ADR。

1. Follow
存在适用 ADR 时，遵循其决策。
只有当前需求确实要求改变该决策时，才修改或新增 ADR。

1. Create

不存在适用 ADR 时：

- 长期影响系统 → 创建 ADR
- 局部实现问题 → 直接实现，不创建 ADR

1. Scope
ADR 只记录：
Context
Decision
Alternatives
Rationale
Consequences

不记录 Work Log、实现过程、测试过程或普通代码变更。

1. Stop
决策已经足够明确、没有明显更优方案且风险可接受时，停止分析并执行。不建立ADR索引

---
