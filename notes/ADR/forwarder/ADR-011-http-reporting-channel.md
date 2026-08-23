# ADR-011: HTTP 上报北向通道（Forwarder 模块扩展）

- 日期: 2026-08-07 | 状态: 已实施（2026-08-10），P1~P5 全部落地 | 用途: 打通 Transport/HTTP 空架子（IHttpClient 无调用方），MQTT 之外提供第二条北向通道
- 范围: Storage（IForwardBuffer 接口只增不删）、Persistence（SqliteForwardBuffer + M007 迁移）、Forwarder（新 HttpForwarderEngine）、Collection（DataDispatcher 入队路由）、Webapi（配置 + 健康检查）、Transport.HTTP（复用现成）

## 设计
- P1 数据路由: forward_buffer / dead_letters 增加 `Channel` 列（M007 迁移，默认 'mqtt'，旧行不迁移数据）；`IForwardBuffer` 新增 `DequeueAsync(int maxCount, string channel)` 重载，旧方法委托 channel="mqtt"（接口只增不删）
- P2 转发引擎: 新增 `HttpForwarderEngine`（BackgroundService，复用 ForwarderEngine 骨架：5s 周期、批量出队、Commit/MarkFailed、死信）；DI 按配置 `Forwarder:Channels`（mqtt | http | both）决定注册 MQTT 与/或 HTTP 引擎，默认 mqtt 行为不变
- P3 入队路由: DataDispatcher 按同一配置把批次写入对应 channel 队列
- P4 配置与健康: appsettings 新增 `Forwarder:Http`（BaseUrl / AuthType / BearerToken / TimeoutMs / MaxRetries，复用 HttpConnectionOptions）；`/healthz` 增加 HTTP 通道检查（复用 IHttpClient.HealthCheckAsync：Connected→Healthy、Disconnected→Degraded）
- P5 死信: 死信 API 暂不加 channel 过滤（列透传即可，前端不动，后续需要再加）

## 验证
- 新增 HttpForwarderEngineTests（fake IHttpClient：成功→Commit / 失败→MarkFailed / 断线→跳过本轮）+ SqliteForwardBuffer channel 路由单测
- 收尾: build 0 错误 + 全量测试通过

## 风险
- IForwardBuffer 新增方法需同步所有实现（当前仅 SqliteForwardBuffer）；M007 必须保证旧数据默认 mqtt

## 实施记录（2026-08-10）
- P1 数据路由: M008 迁移加 channel 列（默认 'mqtt'）；IForwardBuffer 增 EnqueueAsync/DequeueAsync channel 重载（接口只增不删，旧方法默认 mqtt）；SqliteForwardBuffer 按 (channel, enqueued_at) FIFO 出队
- P2 转发引擎: 新增 HttpForwarderEngine（5s 周期、首轮立即、批量出队 http 通道、Commit/MarkFailed、死信、停机排空）；AddNitroForwarder 改配置驱动（Forwarder:Channels mqtt/http/both），启用 http 时自动注册 IHttpClient（HttpConnectionOptions 由 Forwarder:Http 映射）
- P3 入队路由: DataDispatcher 按同一配置多通道入队，多通道时每通道独立 batchId（避免缓冲主键冲突）
- P4 配置与健康: appsettings 增 Forwarder:Http（BaseUrl/Path/AuthType/BearerToken/TimeoutMs/MaxRetries/HealthPath）；/healthz 增 http 检查（Connected→Healthy、其余→Degraded，仅 http/both 时注册）
- P5 死信: 不做 channel 过滤（列透传，前端不动）
- 验证: HttpForwarderEngineTests×3（成功→Commit / 失败→MarkFailed / 断线→跳过）+ SqliteForwardBuffer channel 路由×2 + DataDispatcher both 路由×1；全量 Unit 387 + Integration 43 全绿
