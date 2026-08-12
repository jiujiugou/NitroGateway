# ADR-035: 架构角色分离——桌面端采集（边缘），Web 平台管理（中心）
- 日期: 2026-08-12 | 状态: 第 0/1/2 步已实施（2026-08-12）；第 3 步待定 | 来源: 产品"开始出现错乱"讨论——ADR-034（中心也采集）+ ADR-033（配置不同步）+ 上行无 site 维度

## 拍板结论（2026-08-12 用户确认）
- 桌面端（NitroGateway.Desktop）= 边缘采集角色：采集 PLC、本地库为运行时配置/缓存、断网续传、上行转发；现场 UI 以桌面端为准（离线可用）
- Web（Webapi + Vue）= 平台管理角色：设备/点位管理、数据展示、告警；中心不采集、不转发
- 中心库数据写点唯一：ingest（遥测/告警入库）；管理 API 只读中心库

## 设计原则（对照大众设计）
- 角色单一职责：边缘不干平台的事，平台不干边缘的事（中心不采集）
- 单写者：中心库只有 ingest 写数据；边缘库只有采集器写
- 中心为配置权威 + 边缘离线可编辑：冲突按 UpdatedAt 仲裁、删除用 tombstone（ADR-033 C 模型）
- 多现场隔离：上行带 siteId，中心按 site 过滤

## 数据流与存储职责（2026-08-12 补充）
- 数据流：PLC → 桌面采集(1s) → 本地 SQLite 双写（measurements 30 天滚动 + forward_buffer 待转发队列）→ 转发(5s)增量消费 → MQTT 上行 `nitrogateway/{siteId}/{deviceId}/measurements`（QoS1，ADR-035 第 1 步）→ 中心 broker → Ingest 订阅 → center.db → Web 读中心库展示
- 存储职责划分：
  - 本地库 = 现场运行缓存 / 离线保险（断网数据不丢，30 天滚动清理），不是平台副本
  - forward_buffer = 待转发队列（发布成功即 Commit 删除），不是全量复制
  - 中心库 = 平台权威数据（长期、跨现场汇总），数据写点唯一 = Ingest；桌面只采不存平台数据，Web 只读中心库
  - 配置：桌面本地运行配置（可离线编辑，dirty 后上报）+ 中心权威配置（ADR-033 阶段 3/4 同步）
- 结论：边缘缓存 + 中心存储是行业标准（离线优先），不是"复写浪费"；中心重复采集/双写才是失误（ADR-034，第 0 步修复）

## 调整路线（按风险从低到高）
- 第 0 步 · 止血（✅ 2026-08-12）：Webapi 加 `Deployment:Mode`（Gateway | Center），Center 模式不注册采集/转发/MQTT 发布（连带跳过 MqttHealthCheck）；docker-compose.center.yml 的 gateway 设 `Deployment:Mode=Center`，修正"唯一写点是 ingest"注释 → 即 ADR-034 修复
- 第 1 步 · 数据流契约（✅ 2026-08-12）：上行 topic 统一 `nitrogateway/{siteId}/{deviceId}/measurements`（告警同理 `…/alarms`），Ingest 从 topic 第三段解析 site 并入库（M009 迁移加 site_id 列），Web 按 site 过滤；siteId 从配置读（桌面 %LocalAppData%，中心 appsettings），SiteOptions.Resolve 保证缺省不产生坏 topic
- 第 2 步 · 配置同步（✅ 2026-08-12）：ADR-033 阶段 3/4 落地（中心下发 UpdatedAt 双向合并 + 现场 outbox 上报 + tombstone；M010 迁移；ConfigSyncController 导出/接收 + SiteConfigSyncService 周期同步）
- 第 3 步 · 可选物理拆分：边缘 Agent 独立进程/镜像（采集+转发+本地诊断），Webapi 瘦身为纯平台管理；单现场一体机保留为 Gateway 模式演示

## 待定项
- siteId 契约：已随第 1/2 步落地（2026-08-12），Web 按 site 过滤的 UI 维度后续随多现场需求补齐
- 第 3 步物理拆分：本期不做，视多现场规模再定
- 中心是否保留采集代码：保留代码、运行时按模式禁用（同一镜像，环境变量切换），不维护两份产物

## 影响
- 行为变更（G1）：Webapi 按模式裁剪模块、上行 topic 加 siteId 属契约变更，实施前逐项确认
- 桌面端与 Webapi 的采集代码暂时并存（Desktop 已有独立宿主），第 3 步后再收敛
