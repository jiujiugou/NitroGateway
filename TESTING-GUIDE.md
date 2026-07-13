# NitroGateway 测试指南

## 测试分层

```
┌─────────────────────────────────────────┐
│ E2E (1-2 个)                            │
│ 完整采集链路: Mock PLC → DB 验证         │
│ 慢 (~5s)，但证明系统能端到端运行          │
├─────────────────────────────────────────┤
│ 集成测试 (4-5 个)                        │
│ SQLite 读写、DLQ、告警 Duration、CSV 导入  │
│ 中等 (~200ms)，验证持久化和跨模块协作      │
├─────────────────────────────────────────┤
│ 单元测试 (8-10 个)                       │
│ 算法/状态机/校验器/转换逻辑               │
│ 极快 (~1ms each)，验证核心逻辑不退化       │
└─────────────────────────────────────────┘
```

---

## 一、单元测试（8 个文件，~15 个测试方法）

### 规则：不碰 IO、不碰网络、不碰线程。纯内存运行。

---

### 1.1 `PointValuePipelineTests` ⭐⭐⭐⭐⭐

**被测**：`PointValuePipeline.Process(deviceId, rawValues)`

**测什么**：

| # | 场景 | 输入 | 期望 |
|---|---|---|---|
| 1 | Float 缩放 | RawValue=1234, ScaleFactor=0.1, ScaleOffset=0 | Value=123.4 |
| 2 | Float 缩放 + 偏移 | RawValue=100, ScaleFactor=1.5, ScaleOffset=10 | Value=160 |
| 3 | 死区过滤 | 上次值=100, Deadband=5, 本次值=103 | 返回 null（变化不足） |
| 4 | 死区触发 | 上次值=100, Deadband=5, 本次值=107 | 生成快照 |
| 5 | 死区禁用 | Deadband=0 | 永远生成快照 |
| 6 | Bool 类型 | RawValue=true | Value=true, Quality=Good |
| 7 | 缩放失败 | 非数值类型做数值缩放 | Quality=Uncertain, ErrorMessage 非空 |

**理由**：采集链路最核心的转换逻辑，一处错误污染所有数据。

---

### 1.2 `ThresholdEvaluatorTests` ⭐⭐⭐⭐⭐

**被测**：`ThresholdEvaluator.Evaluate(value, rule)`

**测什么**：

| # | 操作符 | 输入 | 期望 |
|---|---|---|---|
| 1 | `>` | value=80, threshold=80 | false |
| 2 | `>` | value=81, threshold=80 | true |
| 3 | `>=` | value=80, threshold=80 | true |
| 4 | `>=` | value=79, threshold=80 | false |
| 5 | `<` | value=79, threshold=80 | true |
| 6 | `<=` | value=80, threshold=80 | true |
| 7 | `==` | value=80, threshold=80 | true |
| 8 | `!=` | value=79, threshold=80 | true |
| 9 | `Between` | value=75, [70, 80] | true |
| 10 | `Between` | value=69, [70, 80] | false |
| 11 | `Between` | value=81, [70, 80] | false |
| 12 | 未知操作符 | `???` | false（不抛异常） |

**理由**：告警引擎的基础，一个操作符写错等于告警系统不可用。

---

### 1.3 `CircuitBreakerTests` ⭐⭐⭐⭐⭐

**被测**：`CircuitBreaker`（三态状态机）

**测什么**：

| # | 场景 | 操作 | 期望 |
|---|---|---|---|
| 1 | 正常通行 | 初始化 | State=Closed, IsOpen=false |
| 2 | 触发断开 | RecordFailure × 5 (threshold=5) | State=Open, IsOpen=true |
| 3 | 冷却中 | Open 后 10s | State=Open, IsOpen=true（未到 30s 冷却） |
| 4 | 进入 HalfOpen | Open 后 31s 检查 IsOpen | State=HalfOpen, IsOpen=false（首次放行） |
| 5 | HalfOpen 探测成功 | HalfOpen → RecordSuccess | State=Closed, IsOpen=false |
| 6 | HalfOpen 探测失败 | HalfOpen → RecordFailure | State=Open, 冷却时间翻倍 |
| 7 | 指数退避 | 连续 open→fail→open→fail | 冷却时间 ×2 → ×4 → 上限 5min |
| 8 | HalfOpen 并发保护 | HalfOpen 中第二个请求 | IsOpen=true（已有探测在进行） |
| 9 | Reset 强制恢复 | Open → Reset() | State=Closed, IsOpen=false |
| 10 | RecordSuccess 窗口期 | Closed 状态 3 次失败后 1 次成功 | failureCount 重置，不会触发 Open |

**理由**：工业容错最复杂的逻辑，状态迁移必须精确。这是项目中最容易出 bug 的代码。

---

### 1.4 `ForwardingThrottleTests` ⭐⭐⭐⭐

**被测**：`ForwardingThrottle`（AIMD 算法）

**测什么**：

| # | 场景 | 操作 | 期望 |
|---|---|---|---|
| 1 | 初始状态 | 创建 | MaxBatchSize=1000, DelayMs=0 |
| 2 | 失败收缩 | OnMqttFailure × 3 | MaxBatchSize=125, DelayMs=60 |
| 3 | 触底 | OnMqttFailure × 10 | MaxBatchSize=100（不跌破）, DelayMs=200（不超上限） |
| 4 | 成功恢复 | 触底后 OnMqttSuccess × 20 | MaxBatchSize=1000（回到上限）, DelayMs=0 |
| 5 | ApplyDelay | DelayMs>0 | await 不立即返回 |

**理由**：自适应算法，边界条件（上下限）必须验证。

---

### 1.5 `DeviceHealthMonitorTests` ⭐⭐⭐⭐

**被测**：`DeviceHealthMonitor`

**测什么**：

| # | 场景 | 操作 | 期望 |
|---|---|---|---|
| 1 | 触发 Offline | ReportFailure × 10 | ThresholdReached 触发，status=Offline |
| 2 | 中途成功重置 | 5 次失败 → 1 次成功 → 5 次失败 | 不触发（计数被重置） |
| 3 | 触发 Online 恢复 | 先 Offline → ReportSuccess × 3 | ThresholdReached 触发，status=Online |
| 4 | 快照更新 | ReportSuccess / ReportFailure | Snapshot.LastCollectionAt 更新 |
| 5 | 快照错误信息 | ReportFailure("timeout") | Snapshot.LastError = "timeout" |

**理由**：设备生命周期管理的核心逻辑。

---

### 1.6 `DataTypeExtensionsTests` ⭐⭐⭐

**被测**：`DataTypeExtensions.RegisterCount()`

**测什么**：

| # | 输入 | 期望 |
|---|---|---|
| 1 | Float | 2 |
| 2 | Double | 4 |
| 3 | Int16 | 1 |
| 4 | Int32 | 2 |
| 5 | Bool | 1 |
| 6 | String | 2 |

**理由**：CSV 批量生成和 Modbus 模板依赖它计算地址步长，一处错误导致全部地址偏移。

---

### 1.7 `PointBatchServiceTests` ⭐⭐⭐

**被测**：`PointBatchService.ParseCsv()` 和 `Generate()`

**测什么**：

| # | 场景 | 输入 | 期望 |
|---|---|---|---|
| 1 | 基础 CSV | Name,Address,DataType 三列 | 解析出正确数量 |
| 2 | 带可选列 | ScaleFactor=0.5 | ScaleFactor 生效 |
| 3 | 格式错误行 | DataType=UnknownType | 跳过该行，不影响其他 |
| 4 | 空 CSV | 仅列头无数据 | 返回空 |
| 5 | 名称模板 `AI_{###}` | count=3 | AI_001, AI_002, AI_003 |
| 6 | 地址递增 Float | start=40001, count=3, Float | 40001, 40003, 40005 |
| 7 | 地址递增 Int16 | start=40001, count=3, Int16 | 40001, 40002, 40003 |
| 8 | 导出 CSV | 2 个点位 | 3 行（含列头），含正确值 |
| 9 | 导出含逗号 | Name="Temp,Top" | CSV 字段被引号包裹 |

**理由**：现场工程师每天用的功能，解析和生成逻辑必须正确。

---

### 1.8 `SecurityGuardTests` ⭐⭐⭐

**被测**：`WriteGuard` 及其三个 Validator

**测什么**：

| # | 场景 | 输入 | 期望 |
|---|---|---|---|
| 1 | 正常写入 | DeviceStatus=Online, Value=50, MaxLimit=100 | 通过 |
| 2 | 设备 Offline | DeviceStatus=Offline | 拒绝（ModeValidator） |
| 3 | 超出上限 | MaxLimit=100, Value=150 | 拒绝（RangeValidator） |
| 4 | 低于下限 | MinLimit=10, Value=5 | 拒绝（RangeValidator） |
| 5 | 无范围限制 | MinLimit=null, MaxLimit=null | 通过（RangeValidator 跳过） |
| 6 | 变化率超限 | PreviousValue=50, Value=120（140% 跳变） | 拒绝（RateLimitValidator） |
| 7 | 变化率正常 | PreviousValue=50, Value=75（50% 变化） | 通过 |
| 8 | 首次写入 | PreviousValue=null | 跳过变化率校验，通过 |

**理由**：写指令是最危险的操作，校验逻辑不能有漏洞。

---

## 二、集成测试（4 个文件，~10 个测试方法）

### 规则：需 SQLite（共享内存模式）：无需真实 PLC 或 MQTT Broker。

---

### 2.1 `SqliteForwardBufferTests` ⭐⭐⭐⭐⭐

**被测**：`SqliteForwardBuffer` 完整流程

**测什么**：

| # | 场景 | 期望 |
|---|---|---|
| 1 | Enqueue → Dequeue → Commit | Dequeue 返回同一批次，Commit 后删除 |
| 2 | Dequeue 不删除 | Dequeue 后再次 Dequeue，依然返回（未 Commit） |
| 3 | Dequeue 仅 Pending | DeadLetter 状态的条目不出现在 Dequeue 结果中 |
| 4 | MarkFailed → DLQ | MarkFailed × 5 → status 变为 DeadLetter |
| 5 | RetryDeadLetter | DeadLetter → Retry → status 变回 Pending，retry_count=0 |
| 6 | DiscardDeadLetter | Discard → 条目被删除 |
| 7 | Count | Enqueue 后 +1，Commit 后 -1，DLQ 不计入 Count |
| 8 | Enqueue 包含 retry_count 和 enqueued_at | INSERT 后 retry_count=0 |

**理由**：两阶段提交 + DLQ 是转发链路的核心持久化机制。

---

### 2.2 `SqliteMeasurementStoreTests` ⭐⭐⭐⭐

**被测**：`SqliteMeasurementStore` 读写

**测什么**：

| # | 场景 | 期望 |
|---|---|---|
| 1 | Write → Query | 写入 3 条 → 按时间范围查询 → 返回 3 条 |
| 2 | Query 空结果 | 查询不存在的 deviceId → 返回空列表 |
| 3 | Query 时间过滤 | 写入后查未来时间 → 空 |
| 4 | Purge | 写入旧数据 → Purge(before) → 查询返回空 |
| 5 | 空写入 | Write(空列表) → 返回 Success |

**理由**：时序存储的基础操作。

---

### 2.3 `AlarmEvaluatorTests` ⭐⭐⭐⭐

**被测**：`AlarmEvaluator.Evaluate()` 含 Duration 和去重

**测什么**：

| # | 场景 | 期望 |
|---|---|---|
| 1 | 不满足 Duration 的 Pending | 超限但 DurationSeconds=5，第 1 秒 | 返回 Pending，不生成 Alarm |
| 2 | 满足 Duration → Active | 超限持续 5 秒后 | 返回 Active，生成 Alarm 对象 |
| 3 | 恢复 → Resolved | Active 后值恢复正常 | 返回 Resolved |
| 4 | 去重 | Active 期间再次超限 | 不生成新 Alarm |
| 5 | 多规则 | 同一值匹配 Info+Warning+Critical 三条规则 | 生成三个独立的评估结果 |
| 6 | 禁用规则 | Enabled=false | 不参与评估 |
| 7 | Between 操作符 | Temperature: Operator=Between, [20, 100], value=50 | 触发告警 |

**理由**：告警引擎核心，Duration 状态机和去重逻辑最容易出 bug。

---

### 2.4 `DeviceChangeCacheTests` ⭐⭐⭐

**被测**：`DeviceCache` 增量更新

**测什么**：

| # | 场景 | 期望 |
|---|---|---|
| 1 | 初始化加载 | LoadAsync | 缓存包含所有设备 |
| 2 | GetOnlineDevices | 设备 Online/Offline/Maintenance 混合 | 仅返回 Online |
| 3 | OnDeviceChanged(Added) | 新设备事件 | 缓存 +1 |
| 4 | OnDeviceChanged(Removed) | 删除事件 | 缓存 -1 |
| 5 | OnDeviceChanged(Updated) | 状态变更为 Offline | GetOnlineDevices 不再包含该设备 |

**理由**：配置热加载核心，缓存和数据库之间的一致性。

---

## 三、E2E 测试（1 个文件）

### 3.1 `CollectionPipelineE2ETests` ⭐⭐⭐

**被测**：完整采集链路

**需要**：一个 Mock Modbus TCP 服务器（返回固定寄存器值，~40 行代码）

**测什么**：

| # | 场景 | 期望 |
|---|---|---|
| 1 | 注册设备 → 采集 → 验证落库 | 模拟 PLC 返回 ushort[]{100,200} → SQLite 中有对应的 PointSnapshot |

**理由**：一条 E2E 测试胜过 10 个孤立的单元测试。证明整个系统能跑通。

---

## 四、不需要测试的模块

| 模块 | 原因 |
|---|---|
| **Domain POCOs**（Device, DevicePoint, PointSnapshot 等） | 纯数据容器，无行为 |
| **Controllers**（DevicesController, StatusController 等） | 薄转发层，逻辑在 Manager 中 |
| **BackgroundServices**（CollectionEngine, ForwarderEngine） | 仅调度，逻辑在 Collector/Forwarder 中 |
| **Entity Framework Repositories**（SqliteDeviceRepository 等） | EF Core 本身的集成测试价值低，应在 E2E 中覆盖 |
| **MQTT/HTTP Transport**（MqttClientWrapper, HttpClientWrapper） | 需要真实 Broker，应在 E2E 或手工测试中覆盖 |
| **Protocol Drivers**（ModbusTcpDriver, OpcUaDriver） | 需要真实硬件或模拟器，属于 E2E 范畴 |
| **Program.cs / Host** | 启动代码，不包含业务逻辑 |
| **SignalR Hub**（LiveDataHub） | 两条转发语句 |
| **前端 Vue 组件** | 暂不覆盖（E2E 测试更优先） |

---

## 五、实施顺序

```
Phase 1 (2-3 小时)
├── PointValuePipelineTests     ← 采集核心
├── ThresholdEvaluatorTests     ← 告警核心
├── CircuitBreakerTests         ← 容错核心
└── SqliteForwardBufferTests    ← 转发核心

Phase 2 (2 小时)
├── AlarmEvaluatorTests         ← 告警 Duration
├── ForwardingThrottleTests     ← AIMD 算法
├── DeviceHealthMonitorTests    ← 健康判定
└── DataTypeExtensionsTests     ← 地址步长

Phase 3 (1-2 小时)
├── PointBatchServiceTests      ← CSV 解析+生成
├── SecurityGuardTests          ← 写指令校验
├── SqliteMeasurementStoreTests ← 时序读写
└── DeviceChangeCacheTests      ← 热加载缓存

Phase 4 (1-2 小时)
└── CollectionPipelineE2ETests  ← 端到端 + Mock PLC
```

---

## 六、基础设施

```xml
<!-- tests/NitroGateway.Tests/NitroGateway.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="*" />
    <PackageReference Include="FluentAssertions" Version="*" />
  </ItemGroup>
</Project>
```

集成测试中的 SQLite 使用 `Data Source=:memory:` 共享内存模式，每个测试方法前后自动建表/删表。

---

## 七、目标覆盖率（务实版）

| 层级 | 当前 | Phase 1 | Phase 2 | Phase 1-4 全做完 |
|---|---|---|---|---|
| 单元测试 | 0 | 3 个文件 | 7 个文件 | 8 个文件 |
| 集成测试 | 0 | 1 个文件 | 1 个文件 | 4 个文件 |
| E2E | 0 | 0 | 0 | 1 个文件 |
| **关键路径覆盖** | **0%** | **~35%** | **~55%** | **~70%** |

70% 是务实的目标——剩下的 30% 是前端、MQTT Broker 交互、真实 PLC 协议这些需要真实环境或手工测试覆盖的部分。
