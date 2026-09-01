# ADR-059: MQTT 转发总开关（运行期启停上云转发）

- 日期: 2026-08-19 | 状态: 已实施
- 背景: 用户需要 web 面板一个开关，运行期控制是否 MQTT 上云

## Context

边缘网关形态下，采集数据经 DataDispatcher 双写（本地时序库 + 转发缓冲），Forwarder 每 5s 出队经 MQTT 上云。当前无运行期开关——转发只有配置项 Forwarder:Channels（mqtt/http/both），想停转发只能改配置重启容器。

## Decision

- D1 语义：关闭 = 照常采集 + 照常写本地 SQLite + 告警/web/SignalR 不受影响；仅跳过 MQTT 通道入转发缓冲 → 无缓冲堆积、不触发死信；恢复后从关闭时刻起续传，不补发关闭期数据。
- D2 只控 MQTT：若 Forwarder:Channels 含 http，http 通道不受开关影响（开关仅作用于 mqtt 通道）。
- D3 存储：复用 app_meta 键值表（M006），key='forwarder_mqtt_enabled'、value='true|false'；缺省视为 true（启用）。不改库结构、不加迁移。
- D4 接口：新增 ForwarderController（复用现有 RBAC）：GET /api/forwarder/enabled → 200 { enabled: bool }；PUT /api/forwarder/enabled，body { enabled: bool } → 200。
- D5 实现位置：DataDispatcher.DispatchAsync 入队循环处，对 mqtt 通道检查统一开关接口 IForwardMqttToggle（IsEnabled/SetEnabled），关闭则跳过 mqtt EnqueueAsync（http 照常）。检查逻辑在 Dispatcher 层共用，Webapi 与 Desktop 两个宿主共享同一接口、两套存储：
  - Webapi（Linux 边缘网关/容器形态）：走 app_meta（SQLite，重启保持）；web System 页加 el-switch 绑定 GET/PUT /api/forwarder/enabled。
  - Desktop（WPF 桌面端）：走 DesktopSettings（新增 ForwarderMqttEnabled 字段，存 %LocalAppData%\NitroGateway\desktop-settings.json）；设置页加开关，复用 IDesktopSettingsStore 持久化。

## Alternatives

- 决策点 ① A（只入队不发布）：缓冲仍堆积、触发死信，语义错误。
- 决策点 ① B（跳过入队，选定）：无堆积、不补发。
- 决策点 ② 全通道开关：影响 http 通道，超出需求。

## Rationale

关闭只停 MQTT 上云，本地采集/存储/告警不受影响；不补发避免关闭期数据堆积与重复；复用 app_meta/DesktopSettings 不改库结构。

## Consequences

- web 与桌面均有运行期开关、重启保持；关闭期数据不补发、无死信。
