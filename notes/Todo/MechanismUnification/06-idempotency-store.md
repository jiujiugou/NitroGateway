# 06 · 幂等键存储统一（含跨重启正确性洞）

- 类别：完全统一 + **修正正确性缺陷**
- 优先级：高
- 状态：待办

## 现状

`src/NitroGateway.Command/CommandProcessor.cs:27`：
- 幂等表是**内存态** `ConcurrentDictionary<Guid, CommandAck> _acks`。
- 容量上限 1024，超限粗暴清理（`:132-139`）。
- **进程重启即丢失** → 重放的命令会被重新执行（写 PLC 两次）。
- 依赖"单消费者串行"假设（类注释）。

这违反命令回写的 at-most-once 不变量（ADR-069）。物理写场景下 = 阀门动作两次。

## 统一形态

`IIdempotencyStore`：
- 机制：`TryBegin(key) → ExecuteOnce`；命中已存在则返回既有结果，不重复执行副作用。
- 后端：内存（测试/无持久化）与 **DB 唯一约束**（跨重启）两实现。
- 命令处理改用该接口；DB 后端保证跨重启 at-most-once。

## 差异是参数还是语义

后端选择是参数；"同 key 只执行一次"的语义统一。

## 涉及改动文件（待办）

- [ ] 新增 `IIdempotencyStore` + 内存实现
- [ ] `src/NitroGateway.Command/CommandProcessor.cs` 改用它
- [ ] DB 实现 + **FluentMigrator 迁移**（幂等表，唯一键 commandId）
- [ ] DI 注册（`CommandServiceCollectionExtensions`）

## 验收 / 测试

- 现有 `CommandProcessorTests` / `CommandProcessorPropertyTests` 保持绿。
- 新增：跨重启（重建 processor/store）后同 `commandId` 重放**不再写值**、只回执。
- 并发同 `commandId` 并行投递 → 副作用恰一次（check-then-act 不变量）。

## 约束

DB 变更**必须用 FluentMigrator**（`AGENTS.md`）；`Persistence/Migrations/`。

## 备注

这是清单里唯一一个**已知 correctness 洞**，建议优先于纯重构项。
