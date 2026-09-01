# ADR-023: 连接测试假阳性——从站不存在仍报连接成功

- 日期: 2026-08-09 | 状态: 已实施
- 来源: 用户反馈（UnitId=11 无对应从站仍显示连接成功）

## Context

DevicesController.TestConnection 中 ConnectAsync 成功（TCP 仅建 socket / RTU 仅开串口）即返回 success=true；PingAsync 失败只标记 ping="unreachable"，前端只展示 success 忽略 ping → 从站不存在仍显示"连接成功"。链路通 ≠ 从站存在。

## Decision

- D1 连接成功判定 = Connect 成功 + Ping 成功：Ping 失败 → success=false，error 携带 ping 失败原因。
- D2 计时 sw.Stop() 移至 Ping 之后，latencyMs 覆盖 Connect+Ping 全程。
- D3 Ping 错误信息可读化：ModbusDriverBase.PingAsync 增加 ClassifyPingError —— 超时/断连归"从站无响应（接收数据超时）：请检查从站地址/UnitId、网络与防火墙"，其余归"从站无响应：请检查从站地址/UnitId 与设备状态"；不透传内部细节。

## Alternatives

- 仅 Connect 成功即判定成功：实现简单，但链路通 ≠ 从站存在，假阳性不可接受。
- 透传 HSL 原始内部文本：信息全但不友好，前端暴露实现细节。

## Rationale

链路通只证明 socket/串口可达，从站是否真实应答必须由 Ping 验证；错误文案归因到从站地址/UnitId/网络，便于现场排查，不暴露内部实现。

## Consequences

- Modbus TCP 从站/模拟器若不校验 UnitId，对任意 UnitId 均应答 → 测试仍可能显示成功，属协议现实、无法从客户端消除。
- RTU 从站号不匹配必然无响应（超时），连接测试可靠。
- 失败文案对现场可读，不再透传内部细节。
