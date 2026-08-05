# AGENTS.md — NitroGateway

## 项目描述

工业物联网边缘网关（.NET 10）：PLC 采集（Modbus TCP/RTU、S7）→ 本地 SQLite → MQTT 转发云端 → Vue 3 管理面板。

- 构建: `dotnet build NitroGateway.slnx`; 测试: `dotnet test tests/NitroGateway.UnitTests`（115 通过）
- 入口: `src/NitroGateway.Webapi`（端口 5100）, 前端 5173, 登录 admin/admin123
- 技术栈: .NET 10 / ASP.NET Core / SQLite（EF Core + Dapper + FluentMigrator）/ MQTTnet / Vue 3 + Element Plus + ECharts + SignalR / Docker Compose

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

1. 记忆在文档, 不在聊天: 结论写进 docs/ 或 notes/, 聊天记录不承担记忆。
2. 先跑起来, 再认识: 改代码前先确认构建与测试基线（构建 0 错误 / 115 测试通过）。
3. 功能清单基于代码, 不是记忆: 标注代码位置与触发方式。
4. 风险先记疑点, 验证后下结论: 不做风险评估, 只记 docs/04 疑点与修改雷区。
5. AI 自主交付: 明确交给 AI 的阶段, 人工不介入; AI 交付产物, 用户按需验收。
6. 改动需用户确认: 涉及行为变更、依赖版本、接口调整前, 先说明方案再动手。
7. git 提交由用户执行: AI 不提交。

## 项目规则（轻量混合工作流, 由 project-dev 技能驱动）

### 待办与文档结构

- 需求池 `notes/backlog.md` 只是索引: 每个待办仅保留状态标记（[ ] 未启动 / [s] 已启动 / [x] 已完成）、D-序号、一句话标题、优先级、类型、指向 spec 的链接; 池子分层、巡检与阈值规则见该文件头部。
- 每个待办对应目录 `notes/specs/NNN-标题/`, 内含四份文件（按 notes/templates/ 模板生成, 各控制在一屏内）:
  - `spec.md` — 做什么/为什么: 背景与需求三问、目标、边界、验收标准、工程基线。用户确认对象。
  - `plan.md` — 怎么做: 现状代码位置、方案、风险与对策、假设。用户确认对象。
  - `tasks.md` — 执行清单: 前置、任务分解、执行记录、验收勾选。处理待办 = 推进此文件。
  - `验收指标.md` — 收尾对照: 自动验收 + 行为指标 + 运行时验证 + 基线勾选, 用户最终验收依据。tasks 全勾选后生成。
- 模板只在生成时读一次; 已完成项默认不进上下文, 用户点名才读。

### 流程

- 新想法: backlog 登记 → 按模板生成 spec.md → 按闸门确认 → 生成 plan.md → 按闸门确认 → 生成 tasks.md → 执行 → 生成并勾选验收指标.md → 用户最终验收 → 回写 backlog 为 [x]。
- 一次只做一个: backlog 序号 1 且 [s] 为当前项; 未完成前新想法只入池不实现。
- G1 项未获用户明确确认前, 不建 tasks、不写代码; G2 项无反对即视为通过; G3 项直接执行。
- 完成定义: 验收标准与工程基线全勾选 + 测试绿 + 验收指标.md 全勾选 + docs/notes 同步 + worklog 记录 + backlog 标记 [x]。
- 执行中发现 spec/plan 不成立: 先改 spec/plan 并告知用户, 再继续; 不静默漂移。

### 确认闸门（三级, 按风险分级）

- G1 必须确认（阻塞）: 破坏性操作、接口/数据模型变更、依赖版本、行为变更、安全相关。spec 与 plan 都要用户明确确认。
- G2 默认通过、可反对: 单模块内部优化、测试补充、文档修正、CI 配置、警告治理。只确认 spec 的决策点; 用户不反对即视为通过, 完成时汇报。
- G3 先做后报: 纯机械/纯补充类（格式化、补测试、补注释、改文档）。直接执行, 交付时汇报结果。
- 确认对象是决策点, 不是全文: spec/plan 末尾固定决策点节（≤3 个拍板问题）; 用户回复通过或回答问题即完成确认。
- 批量确认: 积压多个 G2 项时, AI 汇总确认清单（每项 3 行摘要 + 决策点）, 用户一次放行。
- 同一时刻只有当前 [s] 项在确认流程中; 推迟项触发时才确认。

### 验证纪律（验证必须可证伪, 非走过场）

- 有意义的标准: 代码有问题时验证必须失败; 只跑 happy path 不算验证, 失败路径必须有断言。
- 红绿对照: 新测试先证"撤掉修复会红", 再证"修复在则绿"; 旧代码上仍绿的测试 = 没测到点子上, 不算锚定。
- 红绿对照执行法（命令）: ① 备份涉及文件到 specs/NNN/验证记录/_redgreen_backup/（Copy-Item） ② 精确字符串替换撤掉修复 ③ dotnet build + 跑对应测试, 预期红, 原始输出存 验证记录/NNN-redgreen-red.txt ④ 从备份 Copy-Item 恢复并校验修复标志存在 ⑤ 再跑对应测试, 预期绿, 存 NNN-redgreen-green.txt; 全程不提交, 校验不过立即停止。
- 断言外部行为: 测日志关键字/报文字段/进程行为/返回值, 不重述实现; 禁止恒真断言。
- 独立可复现: 验证命令公开固定（build + test）; CI 自动重跑出示结果; 验证记录贴原始命令输出, 不写摘要, 用户可抽查。
### 人机分工（接口约定）

- AI 负责全部流程决策与文档维护: 闸门分级、spec/plan/tasks 生成与推进、状态标记、执行记录、worklog、ADR、提交信息。AI 自主完成, 不逐项请示。
- 用户只做三件事: 定方向（提需求/调整方向）、拍板决策点（回复通过或回答问题）、最终验收（可以/不行）。
- 最终验收依据: 当前项的验收指标.md（自动验收 + 行为指标 + 运行时验证）; 用户回复 通过/不行 即闭环。
- 对话驱动: 用户通过对话交互, 不需要阅读或维护任何笔记文件; 文档由 AI 维护, 用户点名才展示。
- 执行过程必须有记录: AI 把命令+结果、变更、决策依据完整写入 tasks 执行记录 / worklog / ADR, 保证可回溯; 记录供未来查证, 不要求用户阅读。
- 汇报格式: 每轮开场 3 行（当前项 / 上轮结果 / 待拍板决策点）; 每轮收尾 3 行（完成内容 / 验证结果 / 下一步）。

### Token 纪律（必须遵守）

- 只加载当前 [s] 项的四份文件; 不扫描 backlog 历史、已完成项、归档内容。
- 状态推进用勾选/标记, 不整篇重写文件。
- 需求三问（为什么做/验收标准是什么/不做会怎样）在 spec.md 回答, 用户原话记原文。
- 大改动（接口/数据模型/模块边界/依赖版本）先写 ADR（notes/ADR/）。
- 每轮会话结束写 notes/worklog/YYYY-MM-DD.md, 只记结论不记过程。
- 破坏性操作审批: 删除/移动/覆盖/重置文件前, 先列完整清单获明确确认; 禁止通配符批量删除; AI 不执行 git reset --hard / git checkout -- . 等批量回滚。

## 项目警告

- **运行生成物**: `*.db`、`*.db-shm`、`*.db-wal`（SQLite）为运行时文件, 不提交、不手动编辑、不删除; 数据库结构变更走 FluentMigrator 迁移。
- **生成物目录**: `bin/`、`obj/`、`logs/`、`node_modules/`、`dist/` 不修改、不提交。
- **依赖版本**: 不升级/降级依赖包（.NET、Vue、Element Plus 等）, 除非用户明确要求。
- **接口层**: `Storage/`、`Protocol/Abstraction/` 是纯接口, 接口只增不删; 新实现不得改接口层。
- **领域层**: `Domain/` 不引用基础设施, 跨模块通信走事件或接口。
- **未确认项目**: `Infrastructure.Sqlite`、`Scheduler`、`Protocol.Mitsubishi`、`Protocol.OpcUa` 未入 slnx（疑点 Q-01）, 状态确认前不要启用或删除。
- **安全配置**: `appsettings.json` 含内置测试账号与开发 JWT 密钥, 生产环境必须通过环境变量覆盖（见 docker-compose 的 JWT_SECRET）。
