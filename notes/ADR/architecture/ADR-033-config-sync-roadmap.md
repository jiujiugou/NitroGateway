# ADR-033: 桌面↔中心配置同步（阶段 2/3/4）
- 日期: 2026-08-12 | 状态: 阶段 2/3/4 已实施（2026-08-12） | 来源: 2026-08-12 讨论——桌面空库暴露配置不同步；承接 ADR-025 P2（元数据同步/配置下发）与 ADR-029 阶段 2 决策（中心为准全量覆盖）
- 范围: 中心配置导出/接收 API + 桌面同步服务与设置入口；不动测量/告警上行契约（ADR-025/028）

## 三问
- 为什么做: 桌面读本地库、中心读中心库，设备/点位配置两边各写各的；现场拿不到中心已配设备，中心也感知不到现场改动
- 验收: 中心改设备 → 下发后桌面同 Id 生效并采集 → 数据回中心 Web 可见；现场离线新增设备 → 联网上报 → 中心可见；同一设备两边都改 → 中心版本覆盖现场；全程不手改 SQLite
- 不做会怎样: 多现场部署时每台桌面重复手动配置，中心对现场配置无感知，B 方案无法规模化

## 修改权决策（2026-08-12 用户拍板）
- 设备/点位修改权 = 现场临时决定权 + 中心最终裁决权（C 模型）：
  - 现场（桌面）可随时新增/修改/删除设备点位，断网也能改并立即生效（本地库为运行时权威）；改动标记 dirty 后上报中心
  - 中心为最终仲裁者：同一设备两边都改时按 UpdatedAt 合并，中心版本更高则覆盖现场；中心删除为权威删除，现场上报不能复活
  - 分层: 本 ADR 只覆盖设备/点位；告警规则、转发通道、系统参数默认中心为准（另议）
- 下发（中心→现场）不是无条件覆盖: 按 UpdatedAt 双向比对，中心较新才覆盖；现场较新的本地改动保留并待上报

## 关键决策（三阶段共用）
- 设备/点位 Id 全局一致: 同步以 Id 为键 upsert（复用 IDeviceManager.RegisterAsync / IPointManager），禁止两边各自新建——测量按 nitrogateway/{deviceId}/measurements 上行，Id 不一致则 Web 数据对不上
- 离线优先: 桌面本地 SQLite 仍是运行时权威，同步为异步最终一致；不共享库文件
- 鉴权: 中心导出/接收接口过 JWT/RBAC；桌面中心地址与 Token 存 %LocalAppData%（现 SettingsViewModel 为只读展示，需新增配置入口与存储，ADR-029 P5 已定方向）
- 版本字段: devices/points 现无 UpdatedAt（M003 建表），需 FluentMigrator 迁移加列；时间戳统一以中心时钟为准（上报携带现场时间、中心记录取 max；下发携带中心时间，防现场时钟回拨）；删除用 IsDeleted tombstone

## 阶段 2: 中心为准，手动导入（先落地）
- 落地（2026-08-12）：中心 GET /api/devices/export（DevicesController.Export）；桌面设置页中心地址/Token 输入 + 「从中心导入」（SettingsViewModel + CenterConfigClient/CenterConfigImporter/CenterSyncSettingsStore，Token 存 %LocalAppData%\NitroGateway\center-sync.json）；单测覆盖导出/覆盖/取消/鉴权失败
- 中心: DevicesController 加只读快照导出端点（GET /api/devices/export，含 devices+points 全量，JWT 鉴权）——现有 GET /api/devices 与 GET /{deviceId}/points 可直接支撑
- 桌面: SettingsViewModel 加中心地址/Token 输入 + 「从中心导入」按钮；导入语义为「以中心为准重置本地」，覆盖前提示会覆盖本地未上报改动（用户确认）
- 验收: 空库现场导入中心配置后立即出数据

## 阶段 3: 自动下发（中心 → 桌面）
- 落地（2026-08-12）：桌面新增 SiteConfigSyncService（BackgroundService，GatewayHost 注册）：定时（默认 60s，ConfigSync:PollIntervalSeconds 可配，下限 5s）拉取中心快照
- 中心: ConfigSyncController.Export（GET /api/configsync/export，JWT/RBAC）返回全量设备（含 tombstone）+ 中心服务器时间；M010 迁移为 devices/points 加 UpdatedAt/IsDeleted 列
- 合并策略: 按 Id + UpdatedAt 双向比对——中心较新则覆盖本地；本地较新（现场未上报改动）保留并标记待上报；中心显式 tombstone（IsDeleted）才删本地；中心快照缺失但本地存在视为现场临时设备，保留待上报
- 通道先 HTTP 轮询（Transport.HTTP 现成）；MQTT 下行 topic 推送（IMqttClient.SubscribeAsync 已支持，Ingest 同款）作可选实时增强，重连后需全量补拉
- 断网失败静默跳过下次补拉，不阻塞采集；变更日志降 Debug 防刷屏（延续 ADR-030 方向）

## 阶段 4: 现场编辑上报（桌面 → 中心）
- 落地（2026-08-12）：桌面设备/点位增删改后写 config_sync_outbox（M010 建表，DevicesViewModel/PointsViewModel 接入）；SiteConfigSyncService 每周期上报后清行
- 中心: ConfigSyncController.Push（POST /api/configsync/push，Admin/Operator 权限）按 Id UPSERT 并合并 tombstone（中心已删的设备拒绝现场复活）
- 上报结论逐台返回 accepted / skipped（中心 UpdatedAt 较新）/ rejected（中心已删拒绝复活）；三种结论均视为已处理并清 outbox 行，避免死循环重报
- 冲突: 按 UpdatedAt 合并，同时修改中心为准（中心版本更高覆盖现场）；现场上报被中心覆盖后，下次下发以中心版本回写本地并清 dirty

## 验证
- 阶段 2: 桌面单测（导入覆盖/取消/鉴权失败）+ 端到端（中心库配设备 → 桌面导入 → 采集 → Web 见数据）
- 阶段 3/4（已落地 2026-08-12）: SiteConfigSyncServiceTests（双向 UpdatedAt 合并/现场临时设备保留/断网跳过/tombstone/outbox 清行）+ ConfigSyncServiceTests（中心侧合并裁决）+ CenterConfigClientTests 增补同步映射；单测 476 全绿、IntegrationTests 43 全绿、build 0 错误

## 风险
- UpdatedAt 加列需新迁移，旧库默认值兼容
- 多现场同中心时快照按 site 过滤下发，避免全量过大
- 时钟偏差: 统一以中心时间戳为准，避免现场时钟回拨覆盖中心
- 双向写引入冲突面: v1 冲突策略固定「中心为准」，暂不做人工仲裁界面
