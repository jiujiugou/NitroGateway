# AGENTS.md — NitroGateway

## 项目
工业物联网边缘网关（.NET 10）：PLC 采集（Modbus TCP/RTU、S7）→ 本地 SQLite → MQTT 转发云端 → Vue 3 管理面板。

- 构建: `dotnet build NitroGateway.slnx`; 测试: `dotnet test tests/NitroGateway.UnitTests`（基线 130 通过）
- 入口: `src/NitroGateway.Webapi`（端口 5100）, 前端 5173, 登录 admin/admin123
- 技术栈: .NET 10 / ASP.NET Core / SQLite（EF Core + Dapper + FluentMigrator）/ MQTTnet / Vue 3 + Element Plus + ECharts / Docker Compose

## 模块
| 模块 | 职责 | 路径 |
| --- | --- | --- |
| Webapi | REST API + SignalR + 健康检查 + 审计 | src/NitroGateway.Webapi |
| Host | 生命周期、优雅关闭 drain | src/NitroGateway.Host |
| Collection | 采集引擎(1s)、重试、管道、双写、熔断 | src/NitroGateway.Collection |
| Forwarder | MQTT 转发(5s)、AIMD 节流、死信 | src/NitroGateway.Forwarder |
| Device | 设备/点位管理、健康监控 | src/NitroGateway.Device |
| Alarm | 告警规则评估、去抖、通知 | src/NitroGateway.Alarm |
| Domain / Shared | 领域模型、OperationResult | src/NitroGateway.Domain, src/NitroGateway.Shared |
| Persistence | SQLite 实现、迁移 | src/NitroGateway.Persistence |
| Protocol | Modbus / S7 驱动 + 复合工厂 | src/NitroGateway.Protocol |
| Security | JWT + RBAC + WriteGuard + 审计 | src/NitroGateway.Security |
| Telemetry | Prometheus + Activity 追踪 | src/NitroGateway.Telemetry |
| Transport | MQTT / HTTP 客户端 | src/NitroGateway.Transport |
| Storage | 存储纯接口 | src/NitroGateway.Storage |
| web/ | Vue 3 前端 | web/src |

## 雷区（不要违反）
- `*.db`、`*.db-shm`、`*.db-wal` 为运行时文件，不提交、不手动编辑；库结构变更走 FluentMigrator 迁移
- `bin/`、`obj/`、`logs/`、`node_modules/`、`dist/` 不修改、不提交
- 不升级/降级依赖包，除非用户明确要求
- `Storage/`、`Protocol/Abstraction/` 是纯接口，接口只增不删
- `Domain/` 不引用基础设施
- `Infrastructure.Sqlite`、`Scheduler`、`Protocol.Mitsubishi`、`Protocol.OpcUa` 未入 slnx，确认前不启用不删除
- `appsettings.json` 含测试账号与开发 JWT 密钥，生产必须环境变量覆盖

## 轻量规则
1. 动手前三问（对话内一句话，不建文档）：为什么做 / 验收标准是什么 / 不做会怎样
2. G1 确认：破坏性操作、接口/数据模型变更、依赖版本、行为变更、安全相关，先一句话说明再动手；其余直接做
3. 验证：改动附测试，关键逻辑红绿对照，收尾跑构建 + 全量测试
4. 记忆在 notes/：结论写 `notes/worklog/YYYY-MM-DD.md`，待办索引写 `notes/backlog.md`，不建 spec/plan/tasks 文档
5. 扫描/排查出的问题直接写 `notes/ADR/ADR-NNN-标题.md`（问题 + 代码位置 + 修复方向，一屏内），对话不重复罗列；修复时在代码处加注释说明，修完从 ADR 删除该条；网上搜得到的通用知识不记
6. git 提交由用户执行，AI 不提交
7. 详情写注释：类/方法/属性的细节（含义、默认值、边界、设计意图）写进代码 XML 注释、随代码维护；
