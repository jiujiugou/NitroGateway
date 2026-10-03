# ADR-074: 驱动实例单闸门所有权（并发串行化下沉到驱动，装饰器不再持锁）

> **摘要**: 同一驱动实例的 Connect/Disconnect/Read/Write/Ping 共守其自身一把闸门；`ReliableProtocolDriver` 删除 `_connectGate`，Modbus TCP 建连/断开纳入 `Gate`。

- 日期: 2026-09-18 | 状态: 已实施
- 来源: ADR-019（驱动 `_gate` 串行）；协议工厂重写为组合根 `switch`；并发审查发现 `ReliableProtocolDriver.ConnectAsync` 绕过装饰器闸门

## Context

ADR-019 确立：每个具体驱动用自身闸门（Modbus `Gate` / S7 `_gate` / OPC UA `_gate`）串行化对同一非线程安全客户端的访问。

问题出在 `ReliableProtocolDriver`（驱动池返回的装饰器）这一层：

- 装饰器额外持有 `_connectGate`，但只用于读/订阅路径的 `EnsureConnectedAsync`；
- 显式 `ConnectAsync`（写路径 `WriteService`、设备连接测试）与 `DisconnectAsync` 直接透传内层，**绕过**该闸门；
- 于是"写触发的建连"与"读触发的建连"可并发进入内层；而 Modbus TCP 的 `ConnectAsync` 自身无闸门 → 同一 `ModbusTcpNet` 上并发 `ConnectServerAsync`（客户端非线程安全）。

根因：**串行化所有权被拆在两个组件**——装饰器管"生命周期"、驱动管"I/O"，但没有统一规则，导致每个方法都要临时判断归属（"打补丁式修并发"，漏一个方法就出洞）。

## Decision

- **D1 单一所有权**：每个具体驱动实例用**同一把闸门**串行化其**全部**方法（Connect / Disconnect / Read / Write / Ping / Browse / 订阅）。
- **D2 Modbus**：`ModbusDriverBase` 新增 `GuardedAsync`（取 `Gate` 执行）；`ModbusTcpDriver.ConnectAsync/DisconnectAsync` 纳入 `Gate`，建连在闸门内双检 `State`。读/写/Ping 原已走 `Gate`。
- **D3 S7 / OPC UA**：已满足（`_gate` 覆盖含 Connect/Disconnect 的全部方法且门内双检），不改。
- **D4 Modbus RTU**：已满足（`_sync` 串行化租约替换 + 共享串口 `Gate` 串行化帧级访问；同端口跨实例共享本就在 `Gate` 语义内），不改。
- **D5 `ReliableProtocolDriver`**：删除 `_connectGate`；`EnsureConnectedAsync` 收敛为"已连接快路径 + 转调内层 `ConnectAsync`"，串行化交内层。装饰器职责仅剩超时、重试、自动建连编排。

## Alternatives

- **A 保留装饰器闸门并补齐 Disconnect**：两把闸门仍在，Connect 与 Read 之间不互斥，"连到一半被读"窗口未消除；且新增方法仍需逐个判断归属。
- **B 驱动 `Connect` 自建局部锁（不复用 `Gate`）**：生命周期与 I/O 分属两把锁，仍不互斥，同一问题。
- **C 装饰器统一包一层大闸门覆盖全部方法**：与内层闸门重复（双重加锁），且装饰器需转发全部方法，成本高。

## Rationale

- 请求-响应协议（Modbus/S7/OPC UA）在同一条连接上本就应串行，Connect/Disconnect 与 Read/Write 互斥是正确语义。
- 单一所有者使不变式"**结构上无法违反**"：新增方法只要走 `GuardedAsync`（或自家同一把闸门）即可，不依赖逐个自觉。
- 与 ADR-019 一致：装饰器只承担可靠性横切（超时/重试/自动建连），不承担并发原语。

## Consequences

- `ReliableProtocolDriver` 不再持有 `SemaphoreSlim`；并发单飞改由驱动闸门内双检保证。
- Modbus TCP 并发建连（写路径显式 + 读路径自动）串行，消除同一客户端并发 `ConnectServerAsync`。
- 测试：`ProtocolDriverGateConcurrencyTests`（显式 Connect 与读触发建连在驱动闸门内串行，覆盖单飞语义）；释放后快速失败/不排水与释放幂等的系统化检测见 Coyote `ReliableDriverInvariants`（正例 + 负控）。
- 约定：新增协议驱动必须遵守"**实例一把闸门，覆盖全部方法**"；新增可访问底层客户端的受保护方法必须经 `GuardedAsync`。
