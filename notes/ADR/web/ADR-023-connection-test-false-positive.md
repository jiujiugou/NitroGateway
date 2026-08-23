# ADR-023: 连接测试假阳性——从站不存在仍报连接成功

- 日期: 2026-08-09 | 状态: 已修复 | 来源: 用户反馈（UnitId=11 无对应从站仍显示连接成功）
- 范围: src/NitroGateway.Webapi/Controllers/DevicesController.cs、web/src/views/Devices/DeviceForm.vue

## 问题
- TestConnection 中 ConnectAsync 成功（TCP 仅建 socket / RTU 仅开串口）即返回 success=true；PingAsync 失败只标记 ping="unreachable"，前端只展示 success 忽略 ping → 从站不存在仍显示"连接成功"
- 位置: DevicesController.TestConnection 的 ConnectAsync 成功分支

## 修复（2026-08-09）
- Connect 成功 + Ping 失败 → success=false，error 携带 ping 失败原因（链路通 ≠ 从站存在）
- 计时 sw.Stop() 移至 Ping 之后，latencyMs 覆盖 Connect+Ping
- 测试: WebapiControllerTests 新增 3 用例（connect/ping 组合红绿对照），UnitTests 295 通过，build 0 错误

## 限制（不修）
- Modbus TCP 从站/模拟器若不校验 UnitId，对任意 UnitId 均应答 → 测试仍可能显示成功，属协议现实无法从客户端消除
- RTU 从站号不匹配必然无响应（超时），连接测试可靠
## 修复 2: Ping 错误信息可读化（2026-08-09）
- 问题: 修复 1 后失败文案透传 HSL 原始内部文本（PipeTcpNet[127.0.0.1:502] : Socket Exception -> 接收数据超时：5000），前端显示不友好
- 改动: ModbusDriverBase.PingAsync 增加 ClassifyPingError —— 超时/断连归"从站无响应（接收数据超时）：请检查从站地址/UnitId、网络与防火墙"，其余归"从站无响应：请检查从站地址/UnitId 与设备状态"；不透传内部细节
- 测试: 新增 ModbusDriverBaseTests 2 用例（超时/异常原始消息→友好文案，红绿对照）；UnitTests 297 通过