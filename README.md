# NitroGateway — 工业物联网边缘网关

## 一句话

运行在工控机或边缘盒子上的工业协议网关。从 PLC 采集数据 → 本地 SQLite 存储 → MQTT 转发到云端 → Vue 3 管理面板。

## 快速启动

```bash
# 1. 启动 MQTT
docker compose up -d

# 2. 启动后端
cd src/NitroGateway.Webapi && dotnet run

# 3. 启动前端（另开终端）
cd web && npm install && npm run dev

# 4. 打开浏览器
#    前端: http://localhost:5173   登录: admin / admin123
#    API:  http://localhost:5100/swagger
#    指标: http://localhost:5100/metrics
#    健康: http://localhost:5100/healthz
```

Docker 一键部署:
```bash
docker compose up -d --build
# 前端: http://localhost:5170
```

---

## 架构

```
14 个项目，单向依赖，无循环引用

┌─────────────────────────────────────────────────────┐
│                    领域层                            │
│  Domain/         设备、点位、快照、协议抽象          │
│  Shared/         OperationResult (Category+Severity)│
├─────────────────────────────────────────────────────┤
│                    应用层                            │
│  Collection/     采集引擎 + 熔断器 + 通道分发        │
│  Forwarder/      MQTT 转发 + 自适应节流              │
│  Device/         设备/点位管理 + 健康监控 + 批量服务  │
│  Alarm/          告警规则评估 + Duration 去抖        │
├─────────────────────────────────────────────────────┤
│                  基础设施层                          │
│  Persistence/    SQLite 实现 (EF Core + Dapper)      │
│    └ Sqlite/     DbContext、存储、缓冲、告警         │
│  Protocol/       Modbus TCP/RTU、S7 驱动             │
│  Transport/      MQTT (TLS+重连)、HTTP               │
│  Telemetry/      Prometheus + Activity Tracing       │
│  Storage/        纯接口层 (IMeasurementStore 等)     │
│  Security/       JWT + RBAC + WriteGuard + 审计      │
├─────────────────────────────────────────────────────┤
│                    编排层                            │
│  Host/           GatewayLifecycle (关闭时的 drain)   │
├─────────────────────────────────────────────────────┤
│                    表现层                            │
│  Webapi/         REST API + SignalR + HealthChecks   │
│  web/ (Vue 3)    设备管理 + 实时监控 + CSV 导入导出   │
└─────────────────────────────────────────────────────┘
```

## 核心数据流

```
Modbus TCP PLC (:502)
      │
      ▼
DeviceReader ─── 3次重试, 指数退避
      │
      ▼
PointValuePipeline ─── 类型转换, 工程缩放, 死区过滤
      │
      ▼
DataDispatcher ─── 双写: SQLite MeasurementStore + ForwardBuffer
      │
      ├── SQLite (本地时序)
      └── ForwardBuffer ─── FIFO, 两阶段提交, InFlight 防重

ForwarderEngine (5s 周期)
      │
      ▼
Forwarder ─── Dequeue → Serialize(JSON) → MQTT Publish → Commit
      │
      └── 失败 → MarkFailedAsync → retry_count+1 → ≥5 → DeadLetter
```

## 容错设计

### 熔断器 (CircuitBreaker)

```
Closed ──连续5次失败──→ Open ──冷却30s──→ HalfOpen ──探测成功──→ Closed
                          │                    └──探测失败──→ Open (冷却×2,上限5min)
                          │
                     拒绝所有采集请求
```

- 每设备独立熔断器，线程安全 (lock)
- HalfOpen 并发保护：同时只允许一个探测请求
- 探测超时保护：30s 后自动释放锁

### 转发节流 (ForwardingThrottle)

AIMD 自适应算法（和 TCP 拥塞控制同源）:
- 失败 → MaxBatchSize 减半 (下限 100) + DelayMs +20ms (上限 200ms)
- 成功 → MaxBatchSize +10 (上限 1000) + DelayMs -5ms (下限 0)

### 死信队列

- `forward_buffer` 表: Pending → InFlight → Commit(删除) 或 MarkFailed(retry+1)
- retry_count ≥ 5 → DeadLetter, 不再被 Dequeue 取出
- Admin API: 查看/重放/丢弃死信

## 设备健康管理

```
HealthMonitor (单一权威来源 SST)
    │ 计数 成功/失败
    │ 判定 Online/Offline 迁移
    │
    ├── PersistenceListener         → 写数据库
    ├── CircuitBreakerHealthListener → Online 时重置熔断器
    └── SignalRDispatcher           → 推前端 (DeviceStatusChanged)
```

- 3 次连续失败 → Offline, 3 次连续成功 → Online
- 初始 Unknown 状态可自动转换为 Online
- CircuitBreaker 和 HealthMonitor 互不控制: CB 是门控(能不能连), HM 是判定(值不值得信任)

## 告警引擎

```
PointSnapshot ─── AlarmEvaluator ─── 查规则 → 比较 → Duration判定 → Alarm
```

- 阈值比较: `>`, `>=`, `<`, `<=`, `==`, `!=`, `Between`
- Duration 去抖: 值持续超限 N 秒才触发，中间回落则重置计时
- 告警生命周期: Pending → Active → Acknowledged → Resolved
- 去重: Active 期间不重复生成新告警
- 通知: MQTT 推送, `IAlarmNotifier` 可插拔 (钉钉/企微/邮件可扩展)

## 可观测性

| 层次 | 工具 |
|---|---|
| 指标 | Prometheus: `nitro_collection_total`, `nitro_forward_total`, `nitro_circuit_breaker_state`, `nitro_mqtt_state` 等 8 个 |
| 日志 | Serilog: 结构化 JSON, Console + File 日轮转, 自动 TraceId/SpanId |
| 追踪 | Activity (System.Diagnostics): 8 个 Span 覆盖全链路 |
| 健康 | `/healthz` (存活) + `/readyz` (SQLite+MQTT) |
| 设备 | DeviceHealthSnapshot: 最后采集时间/连续失败次数/最后错误 |

## 安全

| 层级 | 实现 |
|---|---|
| 认证 | JWT Bearer, `POST /api/auth/login` 签发, 配置文件用户 |
| 授权 | RBAC: Admin / Operator / Viewer, 5 个策略 |
| 写校验 | WriteGuard: 设备模式 + 值范围 + 变化率三级门控 (FluentValidation) |
| 审计 | AuditMiddleware: 所有 /api/* 访问记录到 Serilog 结构化日志 |
| SignalR | 连接时 query string 传 JWT token |

## 配置热加载

```
Web API → DeviceManager → DB 保存 → StatusChanged 事件
    → 采集线程读 DeviceCache (内存)
    → 当前周期不中断, 下一周期自动切换
```

## API 一览

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/auth/login` | 登录获取 JWT |
| GET/POST/PUT/DELETE | `/api/devices` | 设备 CRUD |
| POST | `/api/devices/{id}/points/import` | CSV 导入点位 |
| GET | `/api/devices/{id}/points/export` | CSV 导出点位 |
| POST | `/api/devices/{id}/points/generate` | 批量生成点位 |
| GET | `/api/measurements/history` | 时序数据查询 |
| GET | `/api/status/system` | 系统状态面板 |
| GET | `/api/status/devices/health` | 设备健康快照 |
| GET | `/api/alarms` | 活跃告警 |
| GET/POST/DELETE | `/api/deadletters` | 死信管理 |
| GET | `/healthz` `/readyz` | 健康检查 |
| GET | `/metrics` | Prometheus 指标 |

## 测试

```bash
dotnet test  # 128 个单元测试

# 核心覆盖:
# - PointValuePipeline: 缩放/死区/类型转换
# - ThresholdEvaluator: 7 种操作符 + Between
# - CircuitBreaker: 三态状态机全路径
# - ForwardingThrottle: AIMD 算法 + 上下界
# - AlarmEvaluator: Duration 计时 + 去重 + 多规则
# - WriteGuard: 三级门控全路径
# - DeviceManager: 状态门控 + FakeRepository
# - PointBatchService: CSV 解析/模板/地址递增/导出
```

## 技术栈

| 层 | 技术 |
|---|---|
| 运行时 | .NET 10 |
| 数据库 | SQLite (EF Core + Dapper + FluentMigrator) |
| 消息 | MQTTnet |
| 指标 | prometheus-net |
| 日志 | Serilog |
| 校验 | FluentValidation |
| 前端 | Vue 3 + Element Plus + ECharts + SignalR |
| 部署 | Docker + docker-compose |
| API 文档 | Swagger / Swashbuckle |

## 模块目录

```
src/
├── NitroGateway.Alarm/        告警引擎
├── NitroGateway.Collection/   采集引擎
├── NitroGateway.Device/       设备管理 + 健康监控
├── NitroGateway.Domain/       领域模型
├── NitroGateway.Forwarder/    MQTT 转发 + 节流
├── NitroGateway.Host/         生命周期管理
├── NitroGateway.Persistence/  SQLite 实现
│   └── Sqlite/                具体实现
├── NitroGateway.Protocol/     协议驱动
│   ├── Abstraction/           接口定义
│   ├── Modbus/                Modbus TCP/RTU
│   ├── S7/                    Siemens S7
│   └── NitroGateway.Protocols/复合工厂
├── NitroGateway.Security/     JWT + RBAC + WriteGuard + 审计
├── NitroGateway.Shared/       OperationResult + 错误分类
├── NitroGateway.Storage/      存储接口(纯抽象)
├── NitroGateway.Telemetry/    Prometheus + Activity
├── NitroGateway.Transport/    MQTT + HTTP 客户端
└── NitroGateway.Webapi/       ASP.NET Core Host

tests/
└── NitroGateway.UnitTests/    128 个单元测试

web/
└── src/                       Vue 3 前端
```

## License

MIT
