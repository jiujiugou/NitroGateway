# ADR-026: WPF 现场采集端设计（B 方案）

- 日期: 2026-08-10 | 状态: 已实施（P0/P1 完成，P2 待办） | 来源: 上位机方向——B 方案（多现场 → 一中心）现场侧桌面；采集/协议/转发/告警复用现有类库；云端侧见 ADR-025
- 范围: 新增 `src/NitroGateway.Desktop`（WPF 现场采集端）；MQTT 上报对接 ADR-025 契约；无内嵌 Web（远程查看走中心 Webapi/Vue）

## 设计目标（七问摘要）
- 目标: 每现场一台桌面采集端——本地采集 + 本地存储 + 本地实时显示 + MQTT 上报中心；桌面关不影响中心，断网不丢数据
- 边界: 只做现场侧；中心入库/展示在 ADR-025；不做元数据同步/配置下发（P2）
- 数据流: `Collection`(1s) → `DataDispatcher` → `MeasurementWriteHost`(本地 SQLite) + `Forwarder`(5s 批量 QoS1 → 中心 broker，topic/payload 按 ADR-025)；UI 链路: 服务事件 → EventBridge（200ms 节流）→ ViewModel → Dispatcher → 绑定；历史/最新值走 `IMeasurementStore.QueryLatestAsync` / `QueryPagedAsync`

## 技术选型
- WPF + .NET 10（`net10.0-windows`）；MVVM 用 CommunityToolkit.Mvvm；图表 LiveCharts2；串口 `System.IO.Ports`（P2）
- 宿主: `Host.CreateApplicationBuilder`，`App.xaml.cs` 启动；注册 `AddNitroGatewayHost` + `AddNitroSqlite` + `AddNitroCollection` + `AddNitroForwarder` + `AddNitroAlarm` + `AddNitroProtocol`/`AddNitroModbus`/`AddNitroS7` + `AddNitroMqtt`；IHostedService 照常运行

## 项目结构
```
src/NitroGateway.Desktop/
  App.xaml(.cs)            — 宿主启停、单实例 Mutex、全局异常
  Hosting/GatewayHost.cs   — Host 构建 + 模块注册 + drain（复用 GatewayLifecycle）
  Messaging/EventBridge.cs — 服务事件 → UI 事件（Channel + 200ms 帧刷新）
  ViewModels/              — Main/Devices/Realtime/Alarms/Settings
  Views/                   — MainWindow + 各页
  Services/UiDispatcher.cs — Dispatcher 封装
```

## 关键决策
- D1 宿主与 Web/网关同一套模块注册扩展 → 现场采集行为三端一致；差异只在"有无 UI + 无内嵌 Web"
- D2 UI 数据通道: 服务事件 → EventBridge 合并帧 → ViewModel → Dispatcher；单点数据直刷、批量合并刷新，避免每点切 Dispatcher 卡 UI
- D3 优雅关闭: `MainWindow.Closing` → `IHost.StopAsync`（`GatewayLifecycle.RequestStop` → 采集停 → drain → 转发 flush `forward_buffer` → MQTT 关闭）→ 退出；未 flush 完的数据留待下次启动续传（forward_buffer 持久化，ADR-025 断网语义）
- D4 配置与路径: 复用 `appsettings.json` + 环境变量；`Mqtt__Broker` 指向中心 broker、topic 按 ADR-025 契约；SQLite/日志路径改 `%LocalAppData%\NitroGateway\`（`Environment.SpecialFolder.LocalApplicationData`）
- D5 无内嵌 Web: 远程查看走中心 Webapi/Vue（ADR-025）；桌面仅本机 UI、本机会话免登录；不启用 `AddNitroSecurity`/WriteGuard（现场侧无 Web 攻击面）
- D6 单实例: 命名 Mutex 防重复启动（现场只允许一个采集进程）
- D7 全局异常: `AppDomain.UnhandledException` + `DispatcherUnhandledException` → 日志 + 非阻塞提示，不闪退
- D8 时钟: 上报使用设备时间戳（`BatchMeasurements.ScanStartedAt`/`MeasurementRecord.Timestamp` 现成），中心按 timestamp 排序（ADR-025 D4）；现场与中心时钟不一致不影响展示
- D9 现场可视性: UI 明示 MQTT 连接状态 + 本地缓冲水位（forward_buffer 积压量），断网时现场人员可直观判断

## 失败模式
- 断网 → `forward_buffer` 排队重传（已有，ADR-025 断网语义）
- broker 重启 → `MqttClientWrapper` 自动重连重订阅（已有，ADR-006 P1-2）
- 采集异常 → `CollectionEngine` 已有兜底（不退出）
- UI 卡顿 → 节流 + 异步查询 + 列表虚拟化；10 万点曲线降采样（P2）
- 关窗后台仍写库 → drain 等待超时后强制退出并记日志
- 本地磁盘满 → 已有 DiskGuard（ADR-012）

## 规模与演进
- 几千点 1s: EventBridge 200ms 帧刷新足够；10 万点: 虚拟化 + 曲线降采样 + 分页查询（已有 `QueryPagedAsync`）
- 演进: P2 加串口 Modbus RTU、配置界面（增删设备/点位写库）、元数据同步（对接 ADR-025 P2）

## 后续
- P2 串口 Modbus RTU、配置界面（增删设备/点位写库）、元数据同步（依赖 ADR-025 P2）——本 ADR 只定 P0/P1 现场采集壳形态，P2 属后续演进。
