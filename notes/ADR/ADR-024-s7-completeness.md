# ADR-024: S7 实现不完整——前后端缺口清单

- 日期: 2026-08-09 | 状态: 已修复（2026-08-09） | 来源: 用户反馈（S7 实现不完整，前后端都有问题）
- 范围: src/NitroGateway.Protocol/S7、src/NitroGateway.Device/PointBatchService.cs、src/NitroGateway.Webapi/Controllers/PointImportController.cs、web/src/views/Devices/DeviceForm.vue、web/src/views/Points/PointList.vue、web/src/api/devices.ts

## 处理记录（2026-08-09）

### P1 数据正确性 / 必现失败（已修复）
- P1-1 CpuType 默认值 "S71200" 不在 switch 分支（仅 S-1500/S-300/S-400/S-1200）且无 default → 默认配置连接必抛 SwitchExpressionException：默认值改 "S-1200" + 未知值显式 ArgumentException（ParseCpuType）
- P1-2 ConnectAsync 取消泄漏：建连成功后取消/异常统一 Dispose 局部 client，不再悬挂 PLC 连接
- P1-3 FormatAddress 忽略 M/I/Q 区地址自带类型：S7AddressParser.FormatForHsl 地址自带类型优先 + 类型冲突/位后缀违规显式报错（MW10+Float 不再静默读 MD10）

### P2 配置与健壮性（已修复）
- P2-1 Rack/Slot 改 Convert.ToInt32（支持字符串参数），0-255 越界报错；未知 CpuType 显式报错
- P2-2 PingAddress 位地址（DBX/Mx.y）按 ReadBoolAsync 读，其余按 ReadInt16Async

### P3 前端缺失（已修复）
- P3-1 DeviceForm 增加 S7 参数区（Rack/Slot/CpuType/PingAddress），syncParams 落库 S7 parameters
- P3-2 S7 传输方式固定 TCP（禁用输入框），连接地址占位按协议 102/502
- P3-3 批量生成按协议解释起始地址：GenerateRequest.StartAddress int→string（兼容数字）、Generate 增 Protocol 参数、S7 支持 DB{n}.DBD/DBW/DBB{offset} 按 ByteSize 递增、Bool 位地址显式拒绝；前端 PointList 按设备协议切换默认地址并传 protocol

## 覆盖缺口（已补测试）
- ParseCpuType 默认/已知/未知（红绿对照）✓
- FormatForHsl 兼容/冲突/位地址判定全格式 ✓
- 批量生成 S7 递增/类型冲突/非法地址/Bool 拒绝/Modbus 非法输入 ✓
- 验证: UnitTests 346 通过（+49），build 0 错误，web `npm run build` 通过（2026-08-09）
