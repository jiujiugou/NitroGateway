# ADR-019: Protocol 模块 Code Review 清单

- 日期: 2026-08-09 | 状态: 全部条目已处理（2026-08-09）
- 用途: 对 Protocol 模块全量 code review 产出的问题清单；修复后在代码加注释并删除本条
- 范围: src/NitroGateway.Protocol（Abstraction / Modbus / S7，slnx 内项目）+ 直接消费方 DeviceReader/DeviceCollector；Mitsubishi/OpcUa 未入 slnx，不在本次范围
- 验证基线: `dotnet build` 0 错误；UnitTests 292 通过；IntegrationTests 40 通过（2026-08-09）

## 处理记录（2026-08-09）

### P1 数据正确性
- P1-1 S7 失败读返回默认值 0（静默坏数据）：`S7Driver` 全部读改显式检查 Hsl 结果
  （`ReadCheckedAsync`，对齐 Modbus），失败抛异常转 OperationResult，驱动层不产出伪值

### P2 并发 / 正确性
- P2-1 S7 无读写闸门：`S7Driver` 加 `_gate`（SemaphoreSlim），读/写/连接/断开/Ping 全部串行化
- P2-2 S7 DataType 映射不全、写恒 float：`ReadTypedAsync`/`WriteTypedAsync` 按 DataType 全量映射
  Hsl 读/写方法（Bool/Byte/UInt16/UInt32/Int64/UInt64/Double/String 均覆盖）
- P2-3 S7 地址仅支持 DB 区：`S7AddressParser` 扩展 M/I/Q 区（正则 + S7Address 增加 Area），
  `FormatAddress` 对 M/I/Q 区按点位 DataType 推导 Hsl 类型字符（Bool→位、Byte/String→B、Int16/UInt16→W、其余→D）
- P2-4 重试管线 3s 超时与设备超时错配：`ReliableProtocolDriver` 超时改构造注入
  （`ProtocolDriverFactory` 传 `DeviceConnection.RequestTimeoutMs`，默认 5s），不再硬编码 3s；
  测试可注入小超时/0 重试（Polly 要求 MaxRetryAttempts≥1，为 0 时跳过重试策略）
- P2-5 重试/每轮日志刷屏：`OnRetry` 与 `DeviceReader.ReadDeviceAsync` 每轮日志降 Debug

### P3 一致性与可维护性
- P3-1 S7 全失败返回空成功：`ReadBatchAsync` 全部失败返回 Failure + State=Faulted（与 Modbus 对齐）
- P3-2 `PingAsync` 硬编码 DB1.DBW0：ping 地址可配置（连接参数 `PingAddress`，默认 DB1.DBW0）
- P3-3 连接管理不在闸门内且不响应取消：S7 `ConnectAsync` 走 `ConnectServerAsync` 异步 + 建连后响应取消；
  ModbusTcp 建连后 `ct.ThrowIfCancellationRequested()`；ModbusRtu 租约替换在 `_sync` + 共享端口闸门内串行化
- P3-4 能力声明与实现不符：`S7DriverCapability` 下调 `SupportsBatchRead/Write=false`（当前逐点聚合）
- P3-5 文档漂移：`src/NitroGateway.Protocol/DESIGN.md` 头部标注"历史快照"并列已知漂移
- P3-6 Endpoint 解析不支持 IPv6：新增 `EndpointParser`（Abstraction），支持 `[::1]:502`，
  ModbusTcp/S7 连接解析改用之

## 覆盖缺口（已补测试）
- S7Driver 失败读不产出伪值（各 DataType 分支，红绿对照）✓
- S7 全失败返回 Failure + Faulted ✓
- S7 地址解析 DB/M/I/Q 全格式 ✓
- ReliableProtocolDriver 注入超时（成功/超时/重试）✓
- EndpointParser IPv4/IPv6/非法 ✓
