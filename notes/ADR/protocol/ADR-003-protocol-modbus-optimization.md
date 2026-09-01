# ADR-003: Protocol Modbus 模块优化决策

- 日期: 2026-08-07 | 状态: 已实施

## Context

Modbus 驱动存在批量读类型组错位、写入类型兜底、地址静默回绕、String 长度未定、RTU 超时不可配、部分失败静默吞掉等问题。

## Decision

- D1 批量读按寄存器连续段切分：ModbusBatchPlanner.SplitContiguousSegments，类型组内非连续点位不再从首点连读。
- D2 写入按 DataType 全量映射 HSL 写方法（Bool/Byte/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double/String），不再 Convert.ToSingle 兜底。
- D3 地址解析校验 1..65536，超限抛 ArgumentException，不做 (ushort) 静默回绕。
- D4 String 固定长度 v1=10（DefaultStringLength 常量 + 注释说明协议约定）；点位级 StringLength 配置透传为后续工作。
- D5 RTU 超时可配：SerialPortSettings 增加 ReceiveTimeoutMs/ReadTimeoutMs/WriteTimeoutMs，从连接参数 RequestTimeoutMs 透传，SettingsEqual 纳入超时字段。
- D6 部分失败不再静默吞掉：ReadBatchAsync 部分点位失败时 LogWarning 记录成功/总数。

## Alternatives

- D2 备选：Convert.ToSingle 兜底（实现简单，但精度/类型错误被掩盖）。
- D4 备选：直接支持点位级 StringLength（改动大，v1 无此需求）。

## Rationale

- 分段切分保证非连续同类型点位取值正确；类型全映射保证写入精度；地址校验防止越界污染相邻寄存器；超时参数化使 RTU 现场可调；部分失败可见便于排障。

## Consequences

- 批量读/写各类型语义正确；非法地址启动即报错；RTU 超时可现场调整；String 长度 v1 固定 10 字符（后续按点位配置扩展）。
