# ADR-003: Protocol Modbus 模块优化清单

- 日期: 2026-08-07
- 状态: 全部条目已处理（2026-08-07）
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除对应条目
- 范围: src/NitroGateway.Protocol/Modbus 全模块（ModbusDriverBase / TCP / RTU / SerialPortManager / ModbusAddressParser）

## 处理记录（2026-08-07）

- P1-1 批量读类型组错位：新增 `ModbusBatchPlanner.SplitContiguousSegments`，`ReadRangeAsync` 类型组内按寄存器连续段切分后再批读（同类型非连续点位不再从首点连读）；基于 ModbusTcpServer 的真实 TCP 回环测试验证非连续同类型点位取值正确；顺带移除 ReadRange 死字段 StartOffset/TotalRegisters（P3-2）
- P1-2 写入类型回退：ModbusRtuDriver/ModbusTcpDriver `WriteSingleValueAsync` 按 DataType 全量映射 HSL 写方法（Bool/Byte/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double/String），不再 Convert.ToSingle 兜底；回环测试覆盖 11 种类型写读
- P2-1 地址回绕：`ModbusAddressParser.Parse` 校验 1..65536，超限抛 ArgumentException，不再 (ushort) 静默回绕
- P2-2 String 长度：`ModbusDriverBase.DefaultStringLength=10` 常量 + 注释说明 v1 固定 10 字符协议约定，点位级 StringLength 配置透传为后续工作
- P3-1 GetDistance 死代码：IAddressParser 接口只增不删，ModbusAddressParser 实现加注释保留（后续可统一让 MergeRanges 复用它）
- P3-3 MergeRanges 注释修正为「间隙 ≤ MaxMergeGap 寄存器 → 合并」
- P3-4 RTU 超时：SerialPortSettings 增加 ReceiveTimeoutMs/ReadTimeoutMs/WriteTimeoutMs，ModbusRtuDriver 从连接参数 RequestTimeoutMs 透传，SettingsEqual 纳入超时字段
- P3-5 部分失败：`ReadBatchAsync` 部分点位失败时 LogWarning 记录成功/总数，不再静默吞掉
- 验证: build 0 错；UnitTests 193 通过（上轮 174 + 19）；IntegrationTests 14 通过（上轮 12 + 2）
