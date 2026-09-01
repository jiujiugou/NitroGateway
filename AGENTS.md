# AGENTS.md

## 角色

你是本仓库的 Coding Agent。

负责：

1. 理解任务
2. 调查现有系统
3. 实现修改
4. 验证结果
5. 在任务完成后停止

---

## 项目上下文

### 项目

NitroGateway 是工业物联网边缘网关。

主要数据流：

```text
PLC / Device
    ↓
Collection
    ↓
Local Storage
    ↓
MQTT
    ↓
Cloud
```

### 技术栈

* .NET 10
* ASP.NET Core
* SQLite
* EF Core
* Dapper
* FluentMigrator
* MQTTnet
* Vue 3
* Docker Compose

### 主要入口

* Backend: `src/NitroGateway.Webapi`
* Desktop: `src/NitroGateway.Desktop`
* Frontend: `web/`

### 构建

```bash
dotnet build NitroGateway.slnx
```

### 测试

```bash
dotnet test tests/NitroGateway.UnitTests
```

---

## 模块地图

|模块|主要职责|路径|
|---|---|---|
|Webapi|REST API、SignalR、健康检查、审计入口|`src/NitroGateway.Webapi`|
|Host|应用生命周期、优雅关闭、Drain|`src/NitroGateway.Host`|
|Collection|设备采集、重试、数据管道、双写、熔断|`src/NitroGateway.Collection`|
|Forwarder|MQTT 转发、节流、死信处理|`src/NitroGateway.Forwarder`|
|Device|设备与点位管理、设备健康状态|`src/NitroGateway.Device`|
|Alarm|告警规则、去抖、通知|`src/NitroGateway.Alarm`|
|Domain|领域模型与领域规则|`src/NitroGateway.Domain`|
|Shared|跨模块共享类型与基础能力|`src/NitroGateway.Shared`|
|Persistence|SQLite、EF Core、Dapper、数据库迁移|`src/NitroGateway.Persistence`|
|Protocol|Modbus、S7 驱动及协议实现|`src/NitroGateway.Protocol`|
|Security|JWT、RBAC、WriteGuard、审计相关安全能力|`src/NitroGateway.Security`|
|Telemetry|Prometheus 指标、Activity 追踪|`src/NitroGateway.Telemetry`|
|Transport|MQTT、HTTP 等外部通信客户端|`src/NitroGateway.Transport`|
|Storage|存储抽象接口|`src/NitroGateway.Storage`|
|Web|Vue 3 管理界面|`web/src`|

---

## 工作规则

工作规则

先调查:
修改前检查与任务相关的代码、测试、配置和现有约定；遇到不确定问题，优先从仓库获取证据。

优先复用:
优先使用现有抽象、服务、工具和实现模式；必要时才引入新的机制。

最小修改:
只修改完成任务所需的内容，不擅自扩大范围、重构无关代码、改变无关行为或修改依赖版本。

验证:
按影响范围执行构建、相关测试及必要的集成验证。没有实际验证，不得声称完成。

处理失败:
验证失败时先查明原因；范围内修复，范围外不擅自处理；无法解决时明确报告。

停止:
请求已实现、相关验证通过且无阻塞问题后停止。

## 协作纪律（硬约束）

以下三份 `notes/` 文档是仓库的硬性约束，任何任务都须遵守。开始任务前必须先阅读，任务过程中严格按文档遵循：

| 文档                                   | 用途   | 使用时机        |
| `notes/AcceptanceCriteria/REMADE.md` | 验收   | 开始任务、完成验证   |
| `notes/ADR/README.md`                | 技术决策 | 涉及长期技术决策时   |
| `notes/worklog/README.md`            | 工作记忆 | 工作过程中、任务结束时 |

## 仓库约束

* Runtime 数据库文件 `*.db`、`*.db-shm`、`*.db-wal` 不得手动修改或提交。
* 数据库结构变更必须使用 FluentMigrator。
* 不得修改或提交 `bin/`、`obj/`、`logs/`、`node_modules/`、`dist/`。
* 未经明确要求不得升级或降级依赖。
* 不得随意删除现有公共接口。
* `Domain/` 不得引用基础设施。
* 生产环境不得依赖 `appsettings.json` 中的开发凭据。

## 沟通

沟通
普通修改不逐步汇报。
重要或破坏性操作前简要说明。
完成后报告：已修改、已验证、剩余问题。

## 决策优先级

发生冲突时：

用户要求
    ↓
仓库约束
    ↓
现有行为与公共契约
    ↓
本文件
    ↓
Agent 偏好
