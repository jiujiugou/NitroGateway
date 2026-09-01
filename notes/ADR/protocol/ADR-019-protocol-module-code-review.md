# ADR-019: Protocol 模块 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施

## Context

Protocol 模块全量 code review 发现：S7 失败读返回默认值 0（静默坏数据）、无读写闸门、DataType 映射不全且写恒 float、地址仅支持 DB 区、重试超时硬编码、全失败返回空成功、Ping 硬编码、能力声明与实现不符、文档漂移、Endpoint 不支持 IPv6。

## Decision

- D1 S7 失败读不产伪值：全部读改显式检查 Hsl 结果（ReadCheckedAsync，对齐 Modbus），失败抛异常转 OperationResult，驱动层不产出伪值。
- D2 S7 读写闸门：加 _gate（SemaphoreSlim），读/写/连接/断开/Ping 全部串行化。
- D3 S7 DataType 全量映射：ReadTypedAsync/WriteTypedAsync 覆盖 Bool/Byte/UInt16/UInt32/Int64/UInt64/Double/String。
- D4 S7 地址支持 M/I/Q 区：S7AddressParser 扩展（正则 + S7Address.Area）；FormatAddress 对 M/I/Q 区按点位 DataType 推导 Hsl 类型字符（Bool→位、Byte/String→B、Int16/UInt16→W、其余→D）。
- D5 重试超时注入：ReliableProtocolDriver 超时改构造注入（ProtocolDriverFactory 传 DeviceConnection.RequestTimeoutMs，默认 5s），不再硬编码 3s。
- D6 全失败返回 Failure：S7 ReadBatchAsync 全部失败返回 Failure + State=Faulted（与 Modbus 对齐）。
- D7 Ping 地址可配置：连接参数 PingAddress，默认 DB1.DBW0。
- D8 能力声明修正：S7DriverCapability 下调 SupportsBatchRead/Write=false（当前逐点聚合）。
- D9 EndpointParser（Abstraction）支持 IPv6（[::1]:502），ModbusTcp/S7 连接解析改用之。
- D10 日志降级：OnRetry 与 DeviceReader.ReadDeviceAsync 每轮日志降 Debug。

## Alternatives

- D1 备选：失败返回默认值 0（实现简单，但静默产出坏数据）。

## Rationale

- 驱动层不产伪值是数据可靠性底线；闸门串行化防并发破坏；DataType 全映射与 M/I/Q 区补齐 S7 能力；超时注入使重试可现场调；能力声明与实际一致避免误用。

## Consequences

- S7 失败读不再静默坏数据；读写/连接串行化；M/I/Q 区与全类型可读写；IPv6 端点可连接；能力声明准确（批量读待后续按 DB 区块实现后上调）。
