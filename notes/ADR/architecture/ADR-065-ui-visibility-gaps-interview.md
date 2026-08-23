# ADR-065: 面试可见性能力缺口——A 类 UI 不可见 + B 类真缺（用户管理/操作日志/转发看板/仪表盘）

- 日期: 2026-08-23 | 状态: A1/A2/A3（含 B2/B3/B4 修法）已落地；B1/A5 修法已定案（ADR-066）待实施；剩余 A4 待实施 | 关联: ADR-001（断点续传）、ADR-055（web 缺口先例）、ADR-064（ThingsGateway 基准）、ADR-066（B1/A5 定案）、docs/interview/00、worklog 2026-08-22/2026-08-23
- 一句话结论: 功能不缺，缺的是「可见证据」——最强的能力（断点续传/写审计/RBAC）在 UI 上肉眼不可见；A 类=已有能力补 UI（不引入雷区），B 类=真缺且面试高频，B 的修法即 A 的补法。

## 已落地（2026-08-23，详见 worklog 2026-08-23）
- A1 + B4 仪表盘增强：KPI 卡（设备总数/在线/离线/点位/今日告警/缓冲积压）+ 活跃告警汇总列表；后端新增 `/api/alarms/summary`（`CountOccurredSinceAsync`，今日按本地 0 点起）。
- A2 + B3 转发看板：SystemStatus 加 outbox 水位曲线 + 断点续传事件流（前端 3s 采样推导：断网→堆积→续传→清空，`ForwarderBoard.vue`）。
- A3 + B2 操作日志页：audit_logs 落库（M014 + `SqliteAuditLogStore`，非 GET best-effort）+ `/api/auditlogs` 查询页（时间/操作者/方法/路径/状态码过滤，Admin/Operator）。

## A 类：已有能力，UI 不可见（补 UI 即可）

### A4 告警通知仅 MQTT，无配置 UI
- 现状: `IAlarmNotifier` 只有 MqttAlarmNotifier；钉钉/企微/邮件是「可插拔扩展点」（Alarm/Notification/）无实现、无配置界面。
- 代码位置: `Alarm/Notification/` + AlarmRulesController（规则 CRUD 已有，通知目标未建模）
- 修复方向: 至少接线一个非 MQTT notifier（SMTP 邮件或通用 Webhook，成本低）+ 告警规则表单加「通知渠道」配置。

### A5 RBAC 无用户/权限管理页
- 现状: RBAC（Admin/Operator/Viewer + 5 策略）在 Security 模块完整，但账号来自配置文件（appsettings.json），无 UsersController、无 UI。
- 代码位置: `Security/`（Auth）+ `Webapi/Controllers/AuthController.cs`（仅 login）
- 修复方向: 见 B1。

## B 类：真缺且面试高频（值得做，按此顺序）

### B1 用户/权限管理页（A5 修法）
- 缺口: 面试必问「权限怎么管理」，现状只能答「配置文件写死账号」。
- 修复方向: 已定案见 ADR-066（2026-08-23）——不换全量 Identity，DB 化 users 表（M015）+ UsersController（Admin 专属 CRUD/启停/重置密码/分配角色）+ `web/src/views/Users/UserListView.vue`；Admin/Operator 写策略已有可复用。

## 落地顺序（每落地一条，从本 ADR 删除该条并记 worklog）
- 剩余: B1 用户管理（A5 修法）→ A4 告警通知配置 UI；A1/A2/A3（含 B2/B3/B4）已落地。
- C 类不碰（面试 ROI 负，ADR-064 阶段二已排）: HMI 组态、规则引擎、网关冗余、OTA、OPC UA Server。
