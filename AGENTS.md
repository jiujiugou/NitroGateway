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

## 并发改动

**默认不走重流程。** 只有**同时**满足下面三条才启用并发建模闭环：

- 动的是**共享生命周期状态**（被 ≥2 条执行流访问的可变状态、非线程安全外部对象、队列、取消/关停/释放路径）；
- 错了会**静默、难复现**（普通 code review 看不出来）；
- **爆炸半径大**（关停、持久化、跨服务、不可逆）。

满足时：

1. `/并发建模 <文件>` —— 产出 `notes/Invariants/<组件>.md`（契约 / 不变量+检测器 / 豁免）。
2. `/并发实现 <组件>` —— 先写检测器（seeded-fault 坏实现上必须变红），再实现（绿）。
3. `/并发攻击 <组件>` —— 红队盲测，负控必备且**可执行**；缺陷分类回流（实现错回实现，模型错回建模改文档）。

其余情况：写几个能失败的并发测试 + 正常评审即可。无共享状态的代码须在产出中显式声明非目标。
