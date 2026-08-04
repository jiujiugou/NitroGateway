# AGENTS.md — NitroGateway

## 项目描述

工业物联网边缘网关(.NET 10):PLC 采集(Modbus TCP/RTU、S7)→ 本地 SQLite → MQTT 转发云端 → Vue 3 管理面板。后端 ASP.NET Core Web API,前端 Vue 3 + Element Plus + ECharts + SignalR,部署 Docker Compose。

- 运行时: .NET 10(SDK 10.0.301), ASP.NET Core
- 数据库: SQLite(EF Core + Dapper + FluentMigrator)
- 消息: MQTTnet(broker: eclipse-mosquitto:2)
- 前端: Vue ^3.5 / Vite 8 / TypeScript 6 / Element Plus ^2.14
- 构建: `dotnet build NitroGateway.slnx`; 测试: `dotnet test tests/NitroGateway.UnitTests`(115 通过)
- 入口: `src/NitroGateway.Webapi`(端口 5100), 前端 5173, 登录 admin/admin123

## 项目模块

| 模块 | 职责 | 关键路径 |
| --- | --- | --- |
| Webapi | REST API + SignalR + 健康检查 + 审计中间件 | src/NitroGateway.Webapi |
| Host | 生命周期、优雅关闭 drain | src/NitroGateway.Host |
| Collection | 采集引擎(1s)、重试、管道、双写、熔断 | src/NitroGateway.Collection |
| Forwarder | MQTT 转发(5s)、AIMD 节流、死信 | src/NitroGateway.Forwarder |
| Device | 设备/点位管理、健康监控 | src/NitroGateway.Device |
| Alarm | 告警规则评估、去抖、通知 | src/NitroGateway.Alarm |
| Domain / Shared | 领域模型、OperationResult | src/NitroGateway.Domain, Shared |
| Persistence | SQLite 实现、迁移 | src/NitroGateway.Persistence |
| Protocol | Modbus / S7 驱动 + 复合工厂 | src/NitroGateway.Protocol |
| Security | JWT + RBAC + WriteGuard + 审计 | src/NitroGateway.Security |
| Telemetry | Prometheus + Activity 追踪 | src/NitroGateway.Telemetry |
| Transport | MQTT / HTTP 客户端 | src/NitroGateway.Transport |
| Storage | 存储纯接口 | src/NitroGateway.Storage |
| web/ | Vue 3 前端 | web/src |

## 项目原则

1. 记忆在文档,不在聊天:结论写进 docs/ 或 notes/,聊天记录不承担记忆。
2. 先跑起来,再认识:改代码前先确认构建与测试基线(构建 0 错误 / 115 测试通过)。
3. 功能清单基于代码,不是记忆:标注代码位置与触发方式。
4. 风险先记疑点,验证后下结论:不做风险评估,只记 docs/04 疑点与修改雷区。
5. AI 自主交付:明确交给 AI 的阶段,人工不介入;AI 交付产物,用户按需验收。
6. 改动需用户确认:涉及行为变更、依赖版本、接口调整前,先说明方案再动手。
7. git 提交由用户执行:AI 不提交。

## 项目规则

(开发阶段工作流, 由 project-dev 技能驱动)

- 一次只做一个需求: backlog 顶部未完成前, 新想法只入池, 不实现。
- 需求三问: 为什么做 / 验收标准是什么 / 不做会怎样; 用户直接需求记原话, AI 自发想法必须完整过三问。
- 流程: backlog → spec(notes/specs/, 一页纸) → 用户确认 → tasks(notes/tasks/, 3~5 个可独立验证) → 实现 → AI 自验(命令+结果) → worklog。
- Spec 未获用户确认, 不拆任务、不写代码。
- 大改动(接口/数据模型/模块边界/依赖版本)先写 ADR(notes/ADR/)。
- 需求实现并验证后, 同步更新 docs/02~04(仅行为变化处)并刷新 docs/00-项目导读.html, docs 不留过期结论。
- 每轮会话结束写 notes/worklog/YYYY-MM-DD.md。
- 破坏性操作审批:删除/移动/覆盖/重置文件前,先列出完整清单向用户说明,获明确确认后才执行;禁止通配符批量删除;AI 不执行 git reset --hard / git checkout -- . 等批量回滚操作。

## 项目警告

- **运行生成物**: `*.db`、`*.db-shm`、`*.db-wal`(SQLite)为运行时文件,不提交、不手动编辑、不删除;数据库结构变更走 FluentMigrator 迁移。
- **生成物目录**: `bin/`、`obj/`、`logs/`、`node_modules/`、`dist/` 不修改、不提交。
- **依赖版本**: 不升级/降级依赖包(.NET、Vue、Element Plus 等),除非用户明确要求。
- **接口层**: `Storage/`、`Protocol/Abstraction/` 是纯接口,接口只增不删;新实现不得改接口层。
- **领域层**: `Domain/` 不引用基础设施,跨模块通信走事件或接口。
- **未确认项目**: `Infrastructure.Sqlite`、`Scheduler`、`Protocol.Mitsubishi`、`Protocol.OpcUa` 未入 slnx(疑点 Q-01),状态确认前不要启用或删除。
- **安全配置**: `appsettings.json` 含内置测试账号与开发 JWT 密钥,生产环境必须通过环境变量覆盖(见 docker-compose 的 JWT_SECRET)。