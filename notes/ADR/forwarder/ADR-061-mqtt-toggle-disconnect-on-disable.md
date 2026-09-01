# ADR-061: MQTT 开关关闭时断开连接并停止重连（状态管理监听联动 UI）

- 日期: 2026-08-21 | 状态: 已实施
- 背景: ADR-059 已有转发总开关，但只跳过转发缓冲入队，不碰连接层——关闭后 MQTT 连接/重连照旧，UI 显示「故障/已连接」语义割裂

## Context

用户明确要求：开关关闭 = 不连接、不重试，直接显示「MQTT 已关闭」；做一个状态管理监听，UI 状态改变后通知 MQTT 文本 UI。现状：MqttClientWrapper.ConnectAsync 无开关检查，首连/重连照常；MqttConnectionState.Faulted 仅由重连超限置位；桌面/web 的 MQTT 状态文本 switch 无 Disabled 分支，关闭时显示「故障/未连接」误导；MqttHealthCheck 对非 Connected 一律 Unhealthy，用户主动关闭会被报不健康。

## Decision

- D1 MqttConnectionState 枚举新增 Disabled（加枚举值，不破坏既有 switch/default）。
- D2 IForwardMqttToggle 新增 event Action<bool>? EnabledChanged（接口只增不删）；SqliteForwardMqttToggle / DesktopForwardMqttToggle 的 SetEnabledAsync 持久化成功后仅在实际变更时触发（InitializeAsync 不触发）。
- D3 MqttClientWrapper 注入 IForwardMqttToggle（可选参数，null=恒启用，兼容 Ingest 等未注册开关的宿主）：
  - ConnectAsync 入口：开关关闭 → 直接返回失败且状态保持 Disabled，不置 Connecting、不触发重连。
  - 订阅 EnabledChanged：关闭 → 先 CancelReconnect + SetState(Disabled) 再断开（状态先置 Disabled，避免 OnDisconnectedAsync 看到 Connected 误启重连）；打开 → ConnectAsync 恢复（订阅重放 CleanStart 兜底）。
  - 竞态防护：连接成功瞬间开关被关 → 立即断开并回 Disabled；取消路径不把 Disabled 覆盖成 Disconnected。
- D4 MqttHostedService 监督循环：Disabled 非 Faulted/Disconnected，天然不监督重连（开关重开由 wrapper 自行恢复）；OnStateChanged 加 Disabled 日志分支。
- D5 MqttHealthCheck：Disabled 报 Degraded（用户主动关闭，非故障）。
- D6 UI：桌面 MainViewModel/SettingsViewModel 文本 switch 加 Disabled => "MQTT 已关闭"；web App.vue 顶栏与 SystemStatus.vue 卡片映射 Disabled→「已关闭」。

## Alternatives

- 只改 UI 显示：连接层仍空转重连，状态与语义不一致。
- 关闭后强制断开但状态仍 Connected：语义错误。

## Rationale

关闭 = MQTT 整体停用（转发 + 告警 MQTT 推送 + 入站订阅随连接断开），连接/重连/健康检查/UI 必须一致；Disabled 独立枚举避免与故障/未连接混淆；依赖新增 Transport.MQTT → Storage（纯接口依赖，无环）。

## Consequences

- 关闭后不连接不重试、UI 显示「MQTT 已关闭」、健康检查报 Degraded；恢复后连接重建、订阅重放。
- 采集/本地存储/web/SignalR 不受影响。
