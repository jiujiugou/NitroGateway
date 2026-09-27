---
name: concurrency-modeling
description: "Use ONLY when the user explicitly invokes the concurrency workflow — keywords: 并发建模, concurrency-model, /并发建模, /并发实现, /并发攻击, /concurrency-attack, or a concurrency model under notes/Invariants must guide implementation/attack testing. Keeps the three things that matter: an invariant table, falsifiable detector tests, and an independent adversarial pass. Do NOT use for single-threaded code, pure data structures, or general refactoring."
---

# 并发建模（Concurrency Modeling）

只保留三样真正扛事的东西：**不变量表 / 可失败的检测器 / 一次独立攻击**。其余（审批、验收、校验器）已删除——它们带来的负担大于收益。

## 先问：值得吗？

**默认不走这套流程。** 只有三个问题**都答「是」**才启用：

1. 这次动的是**共享生命周期状态**吗？（被 ≥2 条执行流访问的可变状态、非线程安全外部对象、队列、取消/关停/释放路径）
2. 错了会**静默、难复现**吗？（普通 code review 看不出来）
3. **爆炸半径大**吗？（关停、持久化、跨服务、不可逆）

否则：写几个能失败的并发测试 + 正常评审即可。若组件**没有共享状态**，明确写出「非目标」再停，不要硬套。

## 何时用 / 不用

- **用**：同时满足上面三条的共享生命周期资源。
- **不用**：单线程代码、纯数据结构、无共享状态的工具函数。若判定不用，显式声明**非目标**，不要沉默跳过。

## 术语对照（人话版）

> 规则：**对用户先讲右列大白话，再用左列术语**；禁止只甩术语让用户看不懂。

| 术语 | 人话（先讲这个） |
|---|---|
| 行为契约 | 这个类答应怎么用、不答应什么 |
| 共享资源 | 哪些东西会被多条线程同时碰 |
| check-then-act | "先判断、再操作"两步之间，状态被别人改了 |
| 并发边界 | 到底哪些线程会同时来、在哪儿撞上 |
| 所有权 | 谁、在什么时候、有权销毁它 |
| 裸借用 | 东西借出去了，但没人管"你还在用"时会不会被销毁 |
| 不变量 | 无论怎么并发都必须**永远成立**的事实 |
| 检测器 | 一个测试，能把"不变量被违反"抓出来（没有它，不变量就是空话） |
| 负控 | 把代码故意改坏，看测试是否真变红；不变红 = 测试没能力失败 |
| 假绿 | 测试永远通过、其实根本没在检查 |
| 受控交错 | 用 `Barrier`/信号量等**确定性地**逼出并发顺序，而不是随机 `sleep` 碰运气 |
| seeded fault | 故意写坏的参考实现，专门用来验负控 |
| 同步规则 | 加锁的范围：哪些操作必须锁住、哪些放出锁外 |
| 机制选择 | 用哪种写法（`lock` / `Channel` / 原语…） |
| 豁免 X | 我们**明知做不到、决定接受**的边界（必须写理由 + 风险） |

## 角色（只有两个，都要）

| 角色 | 入口 | 能碰什么 | 目的 |
|---|---|---|---|
| Modeler | `/并发建模` | 只写 `notes/Invariants/**` | 建模，改不了 src |
| Implementer | `/并发实现`（普通 build） | 全开 | 照文档先写检测器(红)→实现(绿) |
| Adversary（子 agent） | `/并发攻击` | 只写 `tests/**` | 盲测攻击，找反例 |

## 工作流

```
/并发建模 <文件|组件>   → 产出 notes/Invariants/<组件>.md（契约 / 不变量+检测器 / 豁免）
/并发实现 <组件>        → 先写检测器(红) → 实现(绿) → 跑测试给原始输出
/并发攻击 <组件>        → Adversary 盲测：每条不变量一条攻击测试 + 负控 → 反例报告
缺陷回流：
   · 实现错（模型明确、代码违反）→ 回 /并发实现
   · 模型错（契约含糊、不变量错/缺）→ 回 /并发建模 改文档（同一条链）
```

## 回流铁律

攻击发现的问题**必须分类**：**实现错**直接回实现；**模型错**必须先改文档、再改代码。禁止打补丁让测试变绿——模型里的错误假设仍在，下个时序再爆。

## 退出条件（"干净"的判据）

1. 每条不变量都有检测器；
2. 每条检测器都通过**负控**（在 seeded-fault 坏实现上确实变红）；
3. 未探索的交错空间已显式记为豁免/局限；
4. Adversary 一轮内无新反例。
未达第 2 条 = 假绿，不算过。

## 禁止清单

1. 禁止"列多种写法却不收敛到 1 个"。
2. 禁止无锁实现而不写失败分析。
3. 禁止自称"线程安全"——必须由检测器证明。
4. 禁止弱化/删除检测器来让测试变绿（等同模型变更）。
5. 禁止把难啃的时序塞进"豁免"当垃圾桶——每条豁免需写理由 + 风险。
6. **注释式负控不算数**：负控必须是可执行测试（seeded-fault），能自动变红。

## 产出模板

见 [references/template.md](references/template.md)。硬性控制在一屏内，用表格不用散文。
**对用户汇报时先给「人话版」解释（是什么 / 会出什么事 / 要决定什么），再给术语表格。**

## 参考

| 文件 | 内容 |
|---|---|
| [references/checklist.md](references/checklist.md) | ①–⑦ 逐条提问，标注【人定】/【AI可起草】 |
| [references/template.md](references/template.md) | 建模文档骨架（契约 / 不变量 / 豁免） |
| [references/detectors.md](references/detectors.md) | 不变量→可执行检测器；负控；受控交错 |
| [references/antipatterns.md](references/antipatterns.md) | 并发反模式 + 非目标 |
