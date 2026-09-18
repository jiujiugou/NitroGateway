# NitroGateway — 工业物联网边缘网关

> 运行在工控机或边缘盒子上的工业协议网关：从 PLC 采集数据 → 本地 SQLite 存储 → MQTT 转发到云端 → Vue 3 管理面板 / WPF 桌面端，并支持云端命令回写闭环（云 → 网关 → PLC，带回执与幂等）。

## 文档索引

| 文档 | 内容 |
| --- | --- |
| [docs/01-需求.md](docs/01-需求.md) | 需求：背景、目标/非目标、角色、功能/非功能需求、出厂门禁、里程碑 |
| [docs/02-架构设计.md](docs/02-架构设计.md) | 架构：分层模块、部署形态、技术选型、核心数据流、容错/安全/可观测 |
| [docs/03-详细设计.md](docs/03-详细设计.md) | 详细设计：领域模型、采集/转发/命令/告警引擎、SQLite 迁移、协议/安全/Web 明细 |
| [docs/04-测试报告.md](docs/04-测试报告.md) | 测试：体系、782 单测 + 51 集成实测、核心覆盖、出厂门禁 |
| [docs/05-部署运维.md](docs/05-部署运维.md) | 部署运维：compose / systemd+看门狗 / GHCR / 环境变量 / 排障 |
| [docs/06-项目复盘.md](docs/06-项目复盘.md) | 复盘：演进主线、ADR 决策地图、做得好/教训/待办 |
| [notes/ADR/README.md](notes/ADR/README.md) | ADR 决策档案导航（讲故事主线 + 必读 Top 12） |
| [FACTORY-TEST.md](FACTORY-TEST.md) | 出厂门禁 T0~T7（P0+P1 全过才放行） |

## 一句话

运行在工控机或边缘盒子上的工业协议网关。从 PLC 采集数据 → 本地 SQLite 存储 → MQTT 转发到云端 → Vue 3 管理面板 / WPF 桌面端。

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

## 部署形态（边缘网关，ADR-054：web 纯边缘化）

web 与桌面端是同一套引擎的两个自包含边缘壳：**web = Linux 网关管理端**（Vue 管理面板），**桌面端 = Windows 边缘**（WPF）。二者定位对等，互不依赖。

边缘网关数据流：本机采集 → 本地 SQLite → MQTT 发布到 broker，谁订阅谁消费（不依赖中心/Ingest）。

**形态 1 · 现场边缘网关 · Linux（`docker-compose.yml`）**：采集 + 本地库 + web 管理面板装在同一台机器（边缘盒子/工控机），适合单点演示/出厂验证/现场运维。
```bash
docker compose up -d --build
```

**形态 2 · 现场边缘网关 · Windows（`src/NitroGateway.Desktop`）**：本机采集 + 本地库 + 本地 UI，MQTT 发布到配置的 broker
（默认 `MQTT:Host=localhost`；对接远端 broker 时用环境变量 `MQTT__Host`/`MQTT__Port` 覆盖）。

**中心（如需，待独立项目，ADR-054）**：web 已不再承担中心库角色；多现场中心如需，另立独立项目（中心 webapi + Ingest）。
`docker-compose.center.yml`、`ingest`、`center.db`、sites 表暂归档不删，需要时再启用。

> 两个 compose 栈使用同一套宿主端口（1883/5100/5200/5170），正常运行二选一；
> 需同时跑时用 `-p` 区分项目并覆盖端口：`docker compose -p center -f docker-compose.center.yml up -d`（再按需改 mqtt/gateway/web 的宿主端口）。

---

## CI/CD（现状说明）

> ⚠️ `.github/workflows` 当前为空：流水线文件已随 commit `7098f80` 删除（ADR-058 已实施后删除）。下述流程描述的是曾建成的发布路径，本地/手动仍可套用，自动化回归需恢复流水线（见 [docs/06-项目复盘.md](docs/06-项目复盘.md)）。

- 曾用 CI（每次 push / PR）：`validate-compose`（校验 4 种 compose 形态）+ `build-server`（ubuntu 服务端 + 集成测试）+ `build-windows`（全量含 WPF Desktop + 全部单测）。
- 曾用 CD（仅 push master / `v*` tag 且前置 job 全绿）：`build-images` 用 Buildx 构建 `gateway`（根 `Dockerfile`）与 `web`（`web/Dockerfile`）两个镜像，推送到 **GHCR**（`ghcr.io/jiujiugou/nitrogateway-gateway` / `nitrogateway-web`）。
- 镜像 tag 策略：`master` → `latest` + `sha-<7>`；`vX.Y.Z` tag → `vX.Y.Z` + `sha-<7>`。

边缘网关部署机从 GHCR 拉取发布产物（不再现场构建）：
```bash
docker compose -f docker-compose.yml -f docker-compose.cd.yml pull
docker compose -f docker-compose.yml -f docker-compose.cd.yml up -d
```
`docker-compose.cd.yml` 仅覆盖镜像来源（`image:` + `build: !reset` + `pull_policy: always`），mqtt/端口/卷/环境变量仍由 `docker-compose.yml` 定义；本地开发仍可直接 `docker compose up -d`（现场构建路径不变）。

> 部署机需先配置 `.env`：`JWT_SECRET` / `ADMIN_PASSWORD` / `OPERATOR_PASSWORD` / `VIEWER_PASSWORD` 必填，生产禁用仓库内测试账号与开发密钥（未设置 compose 直接拒启）。

---

## 架构

```
21 个项目（slnx 收录），单向依赖，无循环引用

┌─────────────────────────────────────────────────────┐
│                    领域层                            │
│  Domain/         设备、点位、快照、协议抽象          │
│  Shared/         OperationResult (Category+Severity)│
├─────────────────────────────────────────────────────┤
│                    应用层                            │
│  Collection/     采集引擎 + 熔断器 + 通道分发        │
│  Forwarder/      MQTT/HTTP 转发 + 固定批量上限       │
│  Command/        命令订阅 + 幂等 + 回执（ADR-069）   │
│  Device/         设备/点位管理 + 健康监控 + 批量服务  │
│  Alarm/          告警规则评估 + Duration 去抖        │
├─────────────────────────────────────────────────────┤
│                  基础设施层                          │
│  Persistence/    SQLite 实现 (EF Core + Dapper)      │
│    └ Sqlite/     DbContext、存储、缓冲、告警         │
│  Protocol/       Modbus TCP/RTU、S7、OPC UA 驱动     │
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
│  Desktop/ (WPF)  Windows 边缘端壳（复用引擎类库）    │
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
PointValuePipeline ─── 类型转换, 工程缩放, 死区过滤, 点位级降频
      │
      ▼
DataDispatcher ─── 双写: SQLite MeasurementStore + ForwardOutbox
      │
      ├── SQLite (本地时序)
      └── ForwardOutbox ─── Pending → InFlight → Commit（两阶段）

ForwarderEngine (5s 周期)
      │
      ▼
Forwarder ─── Dequeue(≤1000) → Serialize(JSON) → MQTT Publish(QoS1) → Commit
      │
      └── 失败 → MarkFailed → retry_count+1 → ≥5 → DeadLetter

命令回写（云 → 网关 → PLC）
      │
      ▼
CommandHostedService 订阅 nitrogateway/+/+/commands (QoS1)
      │
      ▼
CommandProcessor ─── 幂等(commandId) → IWriteService 写值（WriteGuard 门控）→ 回执 commands/ack
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

### 转发限流（2026-08-22 起固定批量上限，替代 AIMD）

- 单次出队 ≤ 1000 批、单轮排水 ≤ 2000 批，防 MQTT 恢复瞬间冲垮 Broker。
- 积压告警：>1000 批记录 Warning（首超立即、之后每 60s，回落后重置）。
- 历史 AIMD 自适应（失败减半 / 成功 +10）已简化移除，代码不再存在 ForwardingThrottle。

### 死信队列

- `forward_outbox` 表: Pending → InFlight → Commit(删除) 或 MarkFailed(retry+1)
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
| 指标 | Prometheus: `nitro_collection_total`、`nitro_forward_total`、`nitro_circuit_breaker_state`、`nitro_mqtt_state` 等 16 个 |
| 日志 | Serilog: 结构化 JSON, Console + File 日轮转, 自动 TraceId/SpanId |
| 追踪 | Activity (System.Diagnostics): 9 个 Span（CollectRound/CollectDevice/ReadDevice/Pipeline/Dispatch/Forward/SqliteWrite/MqttPublish/CommandProcess），OTLP 导出 |
| 健康 | `/healthz` (存活) + `/readyz` (SQLite+MQTT+disk+http 就绪) |
| 设备 | DeviceHealthSnapshot: 最后采集时间/连续失败次数/最后错误 |

## 安全

| 层级 | 实现 |
|---|---|
| 认证 | JWT Bearer, `POST /api/auth/login` 签发, 登录限流 |
| 授权 | RBAC: Admin / Operator / Viewer, 5 个策略 |
| 写校验 | WriteGuard: 设备模式 + 值范围 + 变化率三级门控 (FluentValidation) |
| 审计 | AuditMiddleware: 所有 /api/* 访问记录到 Serilog 结构化日志 |
| SignalR | 连接时 query string 传 JWT token（禁止匿名订阅） |
| 用户 | DB 化 users 表 + 管理接口（ADR-066），首启从配置种子灌入 |
| 生产 | JWT_SECRET / 三账号密码未设置拒启，禁用默认测试账号（ADR-052） |

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
| GET/POST/PUT/DELETE | `/api/alarmrules` | 告警规则 CRUD |
| GET | `/api/alarms` | 告警（活跃/确认） |
| GET/POST/DELETE | `/api/deadletters` | 死信管理（查看/重放/丢弃） |
| GET/POST | `/api/forwarder` | MQTT 转发总开关 |
| GET | `/api/status/system` | 系统状态面板 |
| GET | `/api/status/devices/health` | 设备健康快照 |
| GET | `/api/auditlogs` | 审计日志查询 |
| GET/PUT | `/api/site` | 站点身份（查看/修改/重新生成） |
| GET/POST/PUT/DELETE | `/api/users` | 用户管理（Admin） |
| POST | `/api/write` | 写值（WriteGuard 门控） |
| GET | `/healthz` `/readyz` | 健康检查 |
| GET | `/metrics` | Prometheus 指标 |

## 测试

```bash
dotnet test  # 782 单元测试 + 51 集成测试（2026-08-30 实测全绿）
```

> 注意：本机若常驻 NitroGateway.Webapi 会锁 DLL，导致 `dotnet test` 增量构建 MSB3027 失败；测试前先停进程。

```text
单元测试：  失败: 0，通过: 782，总计: 782
集成测试：  失败: 0，通过: 51，总计: 51
```

核心覆盖:
- PointValuePipeline: 缩放/死区/类型转换/点位级降频
- ThresholdEvaluator: 7 种操作符 + Between
- CircuitBreaker: 三态状态机全路径
- Forwarder: 两阶段提交/死信阈值/固定上限出队
- Command: 命令解析校验/幂等/写值失败回执
- AlarmEvaluator: Duration 计时 + 去重 + 多规则
- WriteGuard: 三级门控全路径
- DeviceManager: 状态门控 + FakeRepository
- PointBatchService: CSV 解析/模板/地址递增/导出

## 技术栈

| 层 | 技术 |
|---|---|
| 运行时 | .NET 10 |
| 数据库 | SQLite (EF Core + Dapper + FluentMigrator) |
| 消息 | MQTTnet |
| 指标 | prometheus-net |
| 日志 | Serilog |
| 追踪 | OpenTelemetry (Activity, OTLP) |
| 校验 | FluentValidation |
| 前端 | Vue 3 + Element Plus + ECharts + SignalR |
| 桌面 | WPF (.NET 10) |
| 部署 | Docker + docker-compose + systemd + 看门狗 |
| API 文档 | Swagger / Swashbuckle |

## 模块目录

```
src/
├── NitroGateway.Alarm/        告警引擎
├── NitroGateway.Collection/   采集引擎
├── NitroGateway.Command/      命令回写（幂等 + 回执）
├── NitroGateway.Device/       设备管理 + 健康监控
├── NitroGateway.Domain/       领域模型
├── NitroGateway.Forwarder/    MQTT 转发 + 限流
├── NitroGateway.Host/         生命周期管理
├── NitroGateway.Persistence/  SQLite 实现
│   └── Sqlite/                具体实现
├── NitroGateway.Protocol/     协议驱动
│   ├── Abstraction/           接口定义
│   ├── Modbus/                Modbus TCP/RTU
│   ├── S7/                    Siemens S7
│   ├── OpcUa/                 OPC UA（初版）
│   └── NitroGateway.Protocols/复合工厂
├── NitroGateway.Security/     JWT + RBAC + WriteGuard + 审计
├── NitroGateway.Shared/       OperationResult + 错误分类
├── NitroGateway.Storage/      存储接口(纯抽象)
├── NitroGateway.Telemetry/    Prometheus + Activity
├── NitroGateway.Transport/    MQTT + HTTP 客户端
├── NitroGateway.Webapi/       ASP.NET Core Host
└── NitroGateway.Desktop/      WPF 桌面端（Windows 边缘）

tests/
├── NitroGateway.UnitTests/    782 个单元测试
└── NitroGateway.IntegrationTests/  51 个集成测试

web/
└── src/                       Vue 3 前端
```

## License

MIT

