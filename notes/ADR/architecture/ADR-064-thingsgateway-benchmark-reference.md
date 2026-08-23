# ADR-064: ThingsGateway 基准参照清单（反写批量分发 / 内存变量 / 事件总线 / 连读模型）

- 日期: 2026-08-22 | 状态: 待实施（仅决策记录，未动代码）| 关联: worklog 2026-08-22、docs/14 写功能实现指南（IWriteService 缺口）
- 一句话结论: 本地 clone `D:\Code\ThingsGateway`（v12/.NET10/Blazor）逐项对照后，NitroGateway 有 4 个值得参照的能力点 + 3 个阶段二不碰项；最高价值 = ①写功能按驱动批量分发（配合 docs/14）②内存变量 C# 表达式造数（无 PLC 端到端联调）。

## 第一优先级（贴合现有缺口，直接可参照）

1. **RPC 反写批量分发**（对应写功能）——TG `src/ThingsGateway.Gateway.Application/Services/Rpc/RpcService.cs` + `Driver/IRpcDriver.cs`
   - 参照点：按「设备→变量→值」批量分发 → 按驱动分组并发写 → 逐点位返回 `OperResult` + RpcLog 审计队列；`ProtectTypeEnum(ReadOnly/ReadWrite)` + `RpcWriteEnable` 点位写开关。
   - 我方现状：`Security/Guard/WriteGuard.cs` + `WriteCommand.cs` 已实现已测试未接线；`PointAccess` 已有；**缺 `IWriteService` 的「按驱动批量聚合+并发」一层**。
   - 修复方向：`WriteService` 内按驱动分组聚合写命令，复用 `driverPool` 并发写，逐点返回结果 + 审计 + SignalR 回推。


## 第二优先级（参考价值高，工作量中等）

3. **GlobalData 事件总线**——TG `GlobalData/GlobalData.cs`：进程内静态事件（DeviceStatusChangeEvent / VariableValueChangeEvent / AlarmChangedEvent），任意模块可订阅。我方已有 EventBridge/SignalR，对照够用性。
4. **VariableSourceRead 连读模型**——TG `Model/VariableSourceRead.cs`：地址连续变量合并成一次报文读。我方已有 `ModbusBatchPlanner`，对照其通用化程度（是否需提升到跨驱动层）。

## 第三优先级（阶段二，先不碰）

5. 网关冗余（主备心跳+变量同步）——TG `Services/Management/Redundant/`
6. 规则引擎（可视化流程节点）——TG `Services/RulesEngine/`
7. 插件热更新 / OTA——TG `Services/Management/Update/`

## 落地顺序（后续每落地一条，从本 ADR 删除该条并记 worklog）

- 先 ② 内存变量（纯新增、低风险、解锁无硬件联调）→ 再 ① 反写批量分发（配合 docs/14 写功能完成闭环）→ ③④ 按需评估。
