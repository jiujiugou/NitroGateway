# ADR-034: 中心形态误跑采集/转发引擎（本地与中心双采集错乱）
- 日期: 2026-08-12 | 状态: 已修复（并入 ADR-035 第 0 步，2026-08-12 实施：Deployment:Mode=Center 裁剪采集/转发/MQTT） | 来源: 用户提问「本地采集，中心也采集，是不是错乱了」

## 问题
- 中心形态（docker-compose.center.yml 的 gateway 服务）复用 Webapi 全量模块：Program.cs 无条件注册 AddNitroCollection / AddNitroForwarder / AddNitroMqtt，且连接串指向与 ingest 共享的 center.db → 中心 Webapi 也会加载 center.db 设备配置去采集并转发
- 后果 1：中心库有设备配置时（ADR-033 导入/上报后很常见），中心真的去连 PLC——网络可达则与现场重复采集同一设备；不可达则空转重试/熔断/日志刷屏
- 后果 2：Forwarder 以 `nitrogateway/{deviceId}/measurements`（Forwarder.cs:105，无 site 维度）发布，与现场桌面上报 topic 完全一致；Ingest 收到两路同 Id 数据，INSERT OR IGNORE 谁先到谁入库，Web 数据错乱/互相覆盖
- 后果 3：center.db 实际有两个写点（ingest + gateway 采集引擎），与 compose 注释「中心库唯一写点是 ingest」不符；告警侧同样存在中心评估+发布与现场告警混入

## 代码位置
- src/NitroGateway.Webapi/Program.cs（AddNitroForwarder/AddNitroCollection/AddNitroMqtt 无条件注册）
- src/NitroGateway.Forwarder/Forwarder.cs:105（上行 topic 无 site 维度）
- docker-compose.center.yml（gateway 服务连接 center.db 并启动 MQTT）

## 修复方向
- Webapi 按部署形态裁剪模块：新增配置（如 Deployment:Mode，默认 Gateway；中心置 Center）条件注册采集/转发/MQTT；禁用时同步跳过 MqttHealthCheck，管理/查询/SignalR 不受影响
- docker-compose.center.yml 的 gateway 服务设 Deployment:Mode=Center；更正「中心库唯一写点是 ingest」注释为「中心 Webapi 仅读不采」
- 上行契约（ADR-025 topic）v1 不改；多现场来源区分留待 ADR-033 阶段 4（siteId 维度）
