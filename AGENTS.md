# AGENTS.md

## 项目

NitroGateway 是工业物联网边缘网关。

```text
PLC/Device → Collection → Local Storage → MQTT → Cloud
```

## 技术栈

.NET 10 / ASP.NET Core / SQLite / EF Core / Dapper / FluentMigrator / MQTTnet / Vue 3

## 构建与测试

```bash
dotnet build NitroGateway.slnx
dotnet test tests/NitroGateway.UnitTests
```

## 模块

| 模块 | 路径 | 说明 |
|------|------|------|
| Webapi | `src/NitroGateway.Webapi` | REST API、SignalR |
| Collection | `src/NitroGateway.Collection` | 设备采集、重试、熔断 |
| Forwarder | `src/NitroGateway.Forwarder` | MQTT 转发、节流 |
| Device | `src/NitroGateway.Device` | 设备与点位管理 |
| Protocol | `src/NitroGateway.Protocol` | Modbus、S7 驱动 |
| Persistence | `src/NitroGateway.Persistence` | 数据库、迁移 |
| Domain | `src/NitroGateway.Domain` | 领域模型 |
| Security | `src/NitroGateway.Security` | JWT、RBAC |
| Web | `web/src` | Vue 3 前端 |

## 工作规则

1. **先调查** — 修改前先读相关代码和测试
2. **最小修改** — 只改任务需要的部分
3. **验证** — 完成后必须构建和测试
4. **停止** — 验证通过后停止

## 约束

- 数据库变更必须用 FluentMigrator
- 不得提交 `bin/`、`obj/`、`node_modules/`
- `Domain/` 不得引用基础设施
- 详细 ADR 见 `notes/ADR/`，按需查阅
