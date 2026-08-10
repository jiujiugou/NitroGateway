# NitroGateway 项目测试标准（FACTORY-TEST）

## 0. 文档目的与判定总则

**目的**：定义验证“项目可以使用、可长期运行”的可量化测试标准，作为出厂验收与版本发布依据。

**适用范围**：工厂环境模拟验收、发布前回归、多设备/长时间运行专项验证。

**判定总则**
- 测试分三级：**P0 基线门禁**（必须全过）/ **P1 核心场景**（多设备、长稳，必须全过）/ **P2 辅助场景**（可记录缺陷后置）
- **放行规则**：P0 + P1 全部通过 → 判定“可用、可长期运行”；任一 P0/P1 不通过 → 判定不通过，修复后回归
- 每次测试的实测数据与结论记录到 `notes/worklog/YYYY-MM-DD.md`

## 1. 测试环境与工具

| 组件 | 工具 | 要求 |
|---|---|---|
| Modbus 从站模拟 | Modbus Slave / ModRSsim2 / pymodbus | ≥10 台，端口 :502，每台 50 点位 |
| MQTT Broker | `eclipse-mosquitto:2`（docker compose） | 端口 :1883 |
| 网络扰动 | clumsy（Windows）/ tc（Linux） | 200ms 延迟 + 5% 丢包 |
| 后端 | `dotnet run --project src/NitroGateway.Webapi` | :5100 |
| 前端 | `cd web && npm run dev` | :5173，登录 admin/admin123 |
| 监控 | `/metrics` `/healthz` `/readyz` + `dotnet-counters` | 观测口径见第 2 节 |

**测试前置条件（保证基线干净）**
- 使用干净 SQLite 数据库：备份/删除 `src/NitroGateway.Webapi/nitrogateway.db*` 后重启，记录初始文件大小
- 记录初始死信数（`GET /api/deadletters`），长稳测试前清理历史死信
- 记录基线指标：启动时刻的内存、backlog、SQLite 大小、`collection_total` 计数

## 2. 观测点与健康判定口径（所有测试共用）

| 观测点 | 来源 | 健康判定 |
|---|---|---|
| 进程存活 | `Get-Process dotnet` / `docker ps` | 全程无退出 |
| 存活检查 | `GET /healthz` | 200 且 `Healthy` |
| 就绪检查 | `GET /readyz` | MQTT 连接时 `Healthy` |
| MQTT 状态 | `nitro_mqtt_state` | 2=Connected 为健康；长时间 1/3/4 视为异常 |
| 采集计数 | `nitro_collection_total{status="success"}` | 随设备数持续增长 |
| 采集耗时 | `nitro_collection_duration_ms` | 均值稳定、无持续上升 |
| 转发积压 | `nitro_buffer_backlog` | 常态 0~5 波动；断连时上涨、恢复后归零；上限 100000 |
| 熔断器 | `nitro_circuit_breaker_state` | 0=Closed 正常；故障设备 1=Open |
| 死信 | `GET /api/deadletters` | 常态不增长；重放可成功 |
| 内存 | `dotnet_total_memory_bytes` / dotnet-counters GC Heap | 8h 后 ≤ 初始 2 倍 |
| 磁盘 | SQLite 文件 + `logs/` 目录 | 8h 增量 ≤50MB；日志仅保留 7 天 |
| 落库失败 | `nitro_store_write_failures_total` | 全程不增长 |

## 3. T0 构建与启动基线（P0，10 分钟）

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T0.1 | 后端构建 | `dotnet build NitroGateway.slnx` | 0 错误 |
| T0.2 | 单元测试 | `dotnet test tests/NitroGateway.UnitTests` | ≥292 全通过 |
| T0.3 | 集成测试 | `dotnet test tests/NitroGateway.IntegrationTests` | ≥40 全通过 |
| T0.4 | 前端构建 | `cd web && npm run build` | vue-tsc 0 错误，vite 构建成功 |
| T0.5 | 启动 | 顺序启动 MQTT → 网关 | ≤15s 内 `/healthz` 200；`/readyz` Healthy；`nitro_mqtt_state=2` |
| T0.6 | 登录 | `POST /api/auth/login` admin/admin123 | 200 且返回 JWT |

## 4. T1 单设备端到端功能（P0，20 分钟）

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T1.1 | 设备注册 | 注册 Modbus/TCP → 127.0.0.1:502 | 设备 Online（≤2 轮采集内） |
| T1.2 | 点位采集 | 添加点位 Temp@40001(Float) | 3s 内 `nitro_collection_total{status="success"}` 增长 |
| T1.3 | 数据入库 | `GET /api/measurements/history` | 返回采集数据，值符合 WriteGuard 校验 |
| T1.4 | MQTT 转发 | 订阅 `nitrogateway/{deviceId}/measurements` | 收到与采集一致的数据 |
| T1.5 | 前端访问 | 仪表盘 + 系统状态页 | MQTT 已连接、设备 Online、数据刷新 |

## 5. T2 多设备并发（P0 核心，30 分钟）

前置：10 个 Modbus 从站 × 每站 50 点位。

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T2.1 | 批量注册 | 注册全部 10 台设备 | 全部 Online，无注册失败 |
| T2.2 | 并发限流 | 观察采集过程 | 同时采集 ≤ `Collection:MaxConcurrency`（默认 5） |
| T2.3 | 采集吞吐 | 观察 10 台设备 | 所有设备 `collection_total` 持续增长，无饿死 |
| T2.4 | 故障隔离 | 关闭其中 3 台从站 | 3 台 CB=Open，其余 7 台采集不受影响（继续增长） |
| T2.5 | 自动恢复 | 恢复 3 台从站 | 3 台 ≤35s 内自动 Online、CB 回 Closed，无需人工干预 |
| T2.6 | 批量点位 | 批量生成 500 点位 | 生成成功，现有采集不中断 |

**判定**：T2.1~T2.6 全过 → 多设备并发能力达标。

## 6. T3 长时间运行 8 小时（P0 核心，8 小时）

前置：1 台设备 × 10 点位 × 1s 采集周期（或沿用 T2 环境），先记录基线指标。

| # | 标准 | 采样方式 | 通过标准 |
|---|---|---|---|
| T3.1 | 进程存活 | 每小时检查 | 全程无退出 |
| T3.2 | 健康检查 | 每小时 `GET /healthz` | 全程 200 |
| T3.3 | 内存稳定 | dotnet-counters GC Heap Size | 结束时 ≤ 初始 2 倍，曲线无持续上升（无泄漏） |
| T3.4 | 转发积压 | `nitro_buffer_backlog` | MQTT 在线期间 ≤5，不持续增长 |
| T3.5 | 死信 | `GET /api/deadletters` | 不产生新死信 |
| T3.6 | SQLite 增长 | 文件大小 | 8h 增量 ≤50MB（10 点位 1s），总量 <200MB |
| T3.7 | 日志轮转 | `logs/` 目录 | 仅保留最近 7 天，不占满磁盘 |
| T3.8 | 数据连续性 | 抽查 measurements 时间戳 | 无大段空洞（断连期除外）；`nitro_store_write_failures_total` 不增长 |
| T3.9 | 重启恢复 | 8h 后 kill -9 再重启 | 数据不丢，日志出现 InFlight→Pending 恢复，补发完成 |

**判定**：T3.1~T3.9 全过 → 长时间运行达标。测试记录至少包含时间点、内存、backlog、SQLite 大小四列实测数据。

## 7. T4 故障恢复（P1，1.5 小时）

### 7.1 网络异常（Modbus 侧）

| # | 场景 | 操作 | 通过标准 |
|---|---|---|---|
| T4.1.1 | 短断 <30s | 关从站 → 等 10s → 恢复 | 10s 内 CB=Open；恢复后 ≤35s 自动 Closed/Online |
| T4.1.2 | 长断 >5min | 关从站 5 分钟 | 冷却时间翻倍至 5min 上限；恢复后自动回 Online |
| T4.1.3 | 网络质量差 | clumsy 加 200ms 延迟 + 5% 丢包 | 采集耗时升高但不崩溃；撤销后恢复 |

### 7.2 MQTT 异常

| # | 场景 | 操作 | 通过标准 |
|---|---|---|---|
| T4.2.1 | 短断 | `docker stop mqtt` 10s 后恢复 | 数据积压不丢，恢复后自动补发清空 |
| T4.2.2 | 反复抖动 | `docker restart mqtt` ×5（间隔 5s） | 每次自动重连成功，不产生死信 |
| T4.2.3 | 长断 15min | 关闭 broker 15 分钟 | 超重试上限的批次进死信；`POST /api/deadletters/{id}/retry` 重放成功 |

### 7.3 进程/数据异常

| # | 场景 | 操作 | 通过标准 |
|---|---|---|---|
| T4.3.1 | 进程 kill | 正常采集后 kill 网关进程 → 重启 | SQLite 未损坏、InFlight 退回 Pending、数据不丢 |
| T4.3.2 | DB 删除 | 删除 nitrogateway.db → 重启 | FluentMigrator 自动建表，重新注册后正常采集 |

### 7.4 配置热加载

| # | 场景 | 操作 | 通过标准 |
|---|---|---|---|
| T4.4.1 | 改设备配置 | 运行中改 IP 为无效地址再改回 | 当前轮不中断，下一轮生效；全程无崩溃 |
| T4.4.2 | 增删点位 | 添加 10 / 删除 5 / 批量生成 500 | 下一轮生效，现有采集不受影响 |
| T4.4.3 | CSV 导入导出 | export → 改 Scale → import | 导出列头正确，导入后 Scale 生效 |

## 8. T5 安全与权限（P1，10 分钟）

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T5.1 | 越权写 | viewer 登录尝试删除设备 | 403 |
| T5.2 | 授权写 | operator 登录确认告警 | 200 |
| T5.3 | 未认证 | 无 Token 访问 `/api/devices` | 401 |
| T5.4 | 审计 | 检查 Serilog 日志 | 所有 `/api/*` 操作有 AUDIT 记录 |
| T5.5 | SignalR | 无 Token 建连 | 拒绝建立 WebSocket |

## 9. T6 前端验收（P2，10 分钟）

| 页面 | 检测项 | 通过标准 |
|---|---|---|
| 登录 | admin/admin123 | 能登录进入仪表盘 |
| 仪表盘 | 设备统计 | 数字与 API 一致 |
| 设备管理 | CRUD | 增删改查正常 |
| 点位管理 | CSV 导入导出 + 批量生成 | 操作成功 |
| 实时监控 | 选设备 | 数据持续刷新 |
| 历史数据 | 时间范围查询 | 返回正确 |
| 系统状态 | MQTT/熔断器/设备健康 | 与 `/metrics` 一致 |
| 告警管理 | 配规则后 | 列表/确认正常 |
| 死信管理 | 列表/重放/丢弃 | 操作成功 |
| Swagger | `/swagger` | 可访问 |

## 10. 最终判定表（验收汇总）

| # | 标准场景 | 级别 | 通过标准 | 结果 |
|---|---|---|---|---|
| 1 | 构建 + 测试 + 启动 | P0 | T0 全过 | [ ] |
| 2 | 单设备端到端 | P0 | T1 全过（采集入库 + MQTT 转发） | [ ] |
| 3 | 10 设备并发 + 隔离 | P0 | T2 全过 | [ ] |
| 4 | 8 小时长稳 | P0 | T3 全过（内存/SQLite/日志/重启恢复） | [ ] |
| 5 | 断网 30s 自动恢复 | P1 | CB Open→Closed，设备自动 Online | [ ] |
| 6 | MQTT 断连节流补发 | P1 | 积压清空、无丢数 | [ ] |
| 7 | 进程 kill 数据不丢 | P1 | 重启后数据完整 | [ ] |
| 8 | 运行时改配置不中断 | P1 | 下一轮生效 | [ ] |
| 9 | RBAC 权限隔离 | P1 | 401/403 生效、审计齐全 | [ ] |
| 10 | 前端页面可用 | P2 | T6 全过 | [ ] |

**放行结论**：P0 + P1 全部通过 → 判定可用；任一项不通过 → 记录缺陷到 `notes/ADR/` 后修复并回归。

## 11. 缺陷分级

| 级别 | 定义 | 示例 | 处理 |
|---|---|---|---|
| P0 | 阻断发布/数据丢失/崩溃 | 启动失败、采集数据丢失、进程崩溃 | 必须修复后放行 |
| P1 | 核心场景不达标 | 长稳内存超 2 倍、故障隔离失效、指标超阈值 | 必须修复 |
| P2 | 体验/文档类 | 前端样式、提示文案 | 可记录后置 |

## 12. 演示场景（验收/面试演示用）

**场景 1 — 断连自动化恢复（2 分钟）**

关掉 Modbus 模拟器 → 看网关日志中 CB 三态变化 → 前端设备变 Offline → 重启模拟器 → 前端自动变 Online。全程无人工操作。

**场景 2 — 数据不丢（1 分钟）**

网关采集一会 → kill 进程 → 重启 → SQLite 数据完整 → ForwardBuffer 无丢失。

**场景 3 — MQTT 节流（1 分钟）**

关 MQTT Broker → ForwardingThrottle 批量从 1000 降到 100 → 开 Broker → 自动恢复 1000。
