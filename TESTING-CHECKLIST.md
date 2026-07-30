# NitroGateway 测试评估报告

**生成时间**: 2026-07-14
**项目规模**: 178 C# 源文件 + 24 前端文件, 15 测试文件 (128 个单元测试), 16 个模块

---

## 总体评分

```
████████████████████░░░░  约 55%

单元测试 ✅ | 组件测试 ⚠️ | 集成测试 ❌ | 系统测试 ⚠️
界面测试 ❌ | 异常测试 ⚠️ | 压力测试 ❌ | 性能测试 ❌
可靠性测试 ❌ | 验收测试 ❌
```

---

## 十维详细

### 1. 单元测试 (Unit Testing) — ✅ 充分

**发现**:
- 框架: xUnit
- 128 个测试方法，覆盖 Pipeline、CircuitBreaker、AlarmEvaluator、ThresholdEvaluator、WriteGuard、ForwardingThrottle、DeviceManager、PointBatchService 等
- 核心业务逻辑: 熔断器三态全路径、告警 Duration/去重、写指令三级门控均有测试
- FakeRepository 模式用于 DeviceManager 测试

**建议**:
- DataDispatcher (双写+错误路由) 缺少测试
- DeviceReader (重试循环) 缺少测试
- SqliteForwardBuffer DLQ 逻辑缺少测试

**验证方法**: `dotnet test`

---

### 2. 组件测试 (Component Testing) — ⚠️ 部分

| 模块 | 状态 | 说明 |
|---|---|---|
| Modbus TCP 驱动 | ⚠️ | 对着模拟器验证通过，无自动化组件测试 |
| MQTT 模块 | ⚠️ | 重连/节流/死信已验证，无自动化 |
| SQLite 存储 | ⚠️ | Dapper 换后 InFlight 写过，无测试验证 |
| 告警引擎 | ⚠️ | 单元测试有，持久化部分无验证 |
| 安全模块 | ⚠️ | WriteGuard 有单元测试，Token 签发无测试 |
| 健康监控 | ⚠️ | HealthMonitor 有单元测试，Listener 注册链未验证 |

**建议**:
- 每个 IO 模块至少一个组件测试 (接真实 SQLite/Modbus 模拟器)
- 告警 SqliteAlarmRepository.Save + Query 验证

**验证方法**: 每个模块单独跑一次数据通路

---

### 3. 集成测试 (Integration Testing) — ❌ 缺失

**发现**: 0 个集成测试文件。

**建议**:
| 优先级 | 场景 | 验证点 |
|---|---|---|
| P0 | SqliteForwardBuffer DLQ 全流程 | Enqueue → Dequeue(InFlight) → MarkFailed ×5 → DeadLetter → Retry |
| P0 | AlarmEvaluator + 真实规则 | Duration 5s → Active, 恢复 → Resolved |
| P1 | 采集全链路 | Mock Modbus → DeviceReader → Pipeline → Dispatcher → SQLite 有数据 |

**验证方法**: 每个场景写一个 xUnit Fact，连真实 SQLite (内存模式)

---

### 4. 系统测试 (System Testing) — ⚠️ 可做但未自动化

**发现**:
- docker-compose ✅ 有三服务 (MQTT + gateway + web)
- Dockerfile ✅ 后端+前端
- curl 冒烟脚本已提供但未形成文件
- 端到端链路手动验证过

**建议**:
| 场景 | 操作 |
|---|---|
| 采集链路 | 模拟器开 → 注册设备 → 查 SQLite 有数据 |
| 故障恢复 | 关模拟器 → 等 Offline → 开模拟器 → 等 Online |
| MQTT 断连 | docker stop mqtt → 验证系统不崩溃、节流生效 |

**验证方法**: `tests/scripts/smoke-test.sh` (待创建)

---

### 5. 界面测试 (UI Testing) — ❌ 缺失

**发现**:
- Vue 3 前端，9 个页面: 登录、仪表盘、设备管理、设备详情、设备表单、点位管理、实时监控、历史数据、系统状态、告警管理、死信管理
- 无自动化 UI 测试 (Playwright/Selenium)
- 手动点过主要页面

**建议**:
- 边缘网关对 UI 测试优先级低
- 至少验证 3 个核心页面: 登录、设备管理、系统状态

**验证方法**: 浏览器打开每个页面，确认不报错、有数据

---

### 6. 异常测试 (Exception Testing) — ⚠️ 部分

| 异常场景 | 设计 | 验证 |
|---|---|---|
| PLC 断连 | CircuitBreaker 三态 + HalfOpen 探测 | ✅ 手动验证过 |
| MQTT 断连 | ForwardingThrottle AIMD + DLQ | ✅ 日志里有 |
| SQLite 磁盘满 | SqliteErrorClassifier → StorageFull/Critical | ❌ 没模拟过 |
| 配置错误 | JwtSecretKey 空值 → 启动崩溃 | ❌ 会直接崩 |
| 模拟器未启动 | DeviceReader 重试 3 次 → 跳过 | ✅ 日志里有 |
| HalfOpen 探测卡死 | CircuitBreaker 30s 超时释放 | ✅ 代码有保护 |
| Channel 满 | SinkDispatcher/OutboxConsumer DropOldest | ✅ 代码有保护 |
| async void 异常 | Listeners 全部 try/catch 包裹 | ✅ |

**建议**:
- 配置校验: JwtSecretKey 为空时启动时报友好错误而非崩溃
- SQLite 磁盘满: 需要实际模拟或信任 SqliteErrorClassifier 的单元测试

**验证方法**: 逐个注入故障，确认系统不崩溃且有明确日志

---

### 7. 压力测试 (Stress Testing) — ❌ 缺失

**发现**:
- 并发限制: SemaphoreSlim(5) 控制设备并发采集
- Channel 有界: SinkDispatcher(1000), Outbox(1000)
- ForwardingThrottle 上限 1000/下限 100
- 未跑过 50 设备、1000 点位、1 小时持续采集

**建议**:
- 至少跑 10 设备 × 100 点位 × 30 分钟持续采集
- 观察: SQLite 文件大小增长、内存占用、Channel 是否有丢弃日志

**验证方法**: 批量生成 1000 个点位，采集 30 分钟，观察 Prometheus 指标

---

### 8. 性能测试 (Performance Testing) — ❌ 缺失

**发现**:
- Prometheus 指标: CollectionTotal, ForwardTotal, CircuitBreakerState, MqttState 等 8 个
- Activity Tracing: 8 个 Span
- 没有性能基线数据

**建议**:
| 指标 | 如何测 | 参考值 |
|---|---|---|
| 采集延迟 | CollectionDurationMs histogram | P50 < 200ms |
| SQLite 写入 | 日志时间戳 | < 50ms/批次 |
| MQTT 转发延迟 | EnqueuedAt → Commit time | < 5s |
| 内存 | dotnet-counters | < 200MB |

**验证方法**: `dotnet-counters monitor` + Prometheus `/metrics`

---

### 9. 可靠性测试 (Reliability Testing) — ❌ 缺失

**发现**:
- GracefulShutdown: GatewayLifecycle 骨架有 + CollectionEngine.StopAsync drain 逻辑
- Channel 关闭: SinkDispatcher.TryComplete, OutboxConsumer ct
- IDisposable 实现: CircuitBreaker, SinkDispatcher, SqliteForwardBuffer
- 未跑过长稳 (24 小时)
- 未验证过 docker stop 是否丢数据

**建议**:
| 场景 | 验证点 |
|---|---|
| docker stop | 缓冲是否清空、数据是否丢 |
| 24h 长稳 | 内存是否增长、SQLite 是否正常 |
| 反复断连 | CircuitBreaker 指数退避是否正常工作 |

**验证方法**: `docker stats` + 观察 Prometheus 内存/GC 指标

---

### 10. 验收测试 (Acceptance Testing) — ❌ 缺失

**发现**:
- Swagger ✅ 可访问
- README.md ✅ 有快速启动 + 架构图 + API 一览
- 无功能验收清单

**建议**: 基于 API 列表生成验收清单

| 功能 | 验证方式 | 状态 |
|---|---|---|
| 设备 CRUD | Swagger 或 curl | ⬜ |
| 点位 CSV 导入/导出 | 浏览器 | ⬜ |
| 批量生成点位 | Swagger POST generate | ⬜ |
| 实时数据查询 | Swagger GET measurements/history | ⬜ |
| 告警查询 | Swagger GET alarms | ⬜ |
| 死信管理 | Swagger GET/DELETE deadletters | ⬜ |
| 系统状态 | Swagger GET status/system | ⬜ |
| 健康检查 | curl /healthz /readyz | ⬜ |
| RBAC 权限 | viewer 登录 → 删设备 | ⬜ |

---

## 优先修复项 (Top 3)

| # | 严重性 | 问题 | 工作量 |
|---|---|---|---|
| 1 | 🔴 | 集成测试 0 个 — SqliteForwardBuffer DLQ 全流程 + Alarm Duration | 2-3 小时 |
| 2 | 🔴 | 配置校验缺失 — JwtSecretKey 空值时启动崩溃 | 30 分钟 |
| 3 | 🟡 | 可靠性验证 — docker stop 一次确认缓冲不丢 | 30 分钟 |

---

## 已知限制 (诚实清单)

| 限制 | 说明 |
|---|---|
| 未连接真实 PLC | Modbus 模拟器 + S7 未验证 |
| OPC UA Connect 抛异常 | 骨架完成，未对真实服务器调通 |
| SignalR 前端推送待验证 | 后端链路全通，前端实时接收之前有 bug，最新版待重新验证 |
| 单进程单实例 | 无集群、无 Leader Election |
| 测试覆盖 55% | 集成/E2E/压力/可靠性均空白 |
