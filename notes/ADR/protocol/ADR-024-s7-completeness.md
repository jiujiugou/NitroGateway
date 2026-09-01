# ADR-024: S7 实现不完整——前后端缺口决策

- 日期: 2026-08-09 | 状态: 已实施
- 来源: 用户反馈（S7 实现不完整，前后端都有问题）

## Context

S7 实现存在前后端缺口：默认 CpuType "S71200" 不在 switch 分支且无 default 导致默认配置连接必抛异常；ConnectAsync 取消泄漏悬挂连接；FormatAddress 忽略 M/I/Q 区地址自带类型导致静默读错区；Rack/Slot 不支持字符串参数；PingAddress 位地址读取方式错误；前端缺 S7 参数区、传输方式可误改、批量生成不按协议解释起始地址。

## Decision

- D1 CpuType 默认值改 "S-1200"（进入既有分支）+ 未知值显式 ArgumentException（ParseCpuType），默认配置不再抛 SwitchExpressionException。
- D2 ConnectAsync 建连成功后取消/异常统一 Dispose 局部 client，不再悬挂 PLC 连接。
- D3 FormatAddress 地址自带类型优先：S7AddressParser.FormatForHsl 类型冲突/位后缀违规显式报错（MW10+Float 不再静默读 MD10）。
- D4 Rack/Slot 改 Convert.ToInt32（支持字符串参数），0-255 越界报错；未知 CpuType 显式报错。
- D5 PingAddress 位地址（DBX/Mx.y）按 ReadBoolAsync 读，其余按 ReadInt16Async。
- D6 前端补齐：DeviceForm 增加 S7 参数区（Rack/Slot/CpuType/PingAddress）并 syncParams 落库；S7 传输方式固定 TCP（禁用输入框）；批量生成按协议解释起始地址（StartAddress int→string 兼容数字、Generate 增 Protocol 参数、S7 支持 DB{n}.DBD/DBW/DBB{offset} 按 ByteSize 递增、Bool 位地址显式拒绝；PointList 按设备协议切换默认地址并传 protocol）。

## Alternatives

- 给 switch 增加 "S71200" 分支做最小修复：掩盖默认值语义含糊，仍留其他缺口。
- 地址类型解释放前端：逻辑分散、各实现不一致，易再出错。

## Rationale

默认值应符合协议既有分支语义；地址自带类型优先避免静默读错数据区；Rack/Slot 兼容字符串是常见配置习惯；前端按协议解释起始地址避免用户配置错误。

## Consequences

- 默认配置可正常连接；类型冲突显式报错而非静默读错区；取消不再悬挂连接。
- 前端 S7 配置完整可用，批量生成按协议正确解释地址。
