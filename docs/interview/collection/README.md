# Collection 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Collection`（采集引擎模块）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/并发/失败路径）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
CollectionEngine（调度）→ DeviceCollector（流水线）→ DeviceReader（读取）
→ PointValuePipeline（转换）→ DataDispatcher 系（分发/异步写入）
→ CircuitBreaker 系（熔断）→ HealthReporter + DeviceHealthMonitor（健康联动）
→ StopAsync（生命周期）→ 遥测 → 开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 引擎 | `src/NitroGateway.Collection/CollectionEngine.cs` | BackgroundService + PeriodicTimer，每轮独立 scope 调度全量采集，负责优雅停止 |
| 采集器 | `src/NitroGateway.Collection/Collector/DeviceCollector.cs` | 单设备 5 步流水线 + 轮内并发限流（SemaphoreSlim 默认 5） |
| 读取器 | `src/NitroGateway.Collection/DeviceReader/DeviceReader.cs` | 取 Enabled 点位，经驱动池复用长连接批量读取 |
| 转换管道 | `src/NitroGateway.Collection/Pipeline/PointValuePipeline.cs` | 原始值→工程值（缩放/死区缓存），Bool/String 透传 |
| 数据分发 | `src/NitroGateway.Collection/Dispatcher/DataDispatcher.cs` | 双写：时序 Channel + 转发缓冲 + 事件推送，互不阻塞 |
| 时序写入宿主 | `src/NitroGateway.Collection/Dispatcher/MeasurementWriteHost.cs` | 有界 Channel(1000, DropOldest) 异步批量落库 |
| 事件分发 | `src/NitroGateway.Collection/Dispatcher/SinkDispatcher.cs` | 有界 Channel(1000) 逐事件独立 scope 推送所有 Sink |
| 健康上报 | `src/NitroGateway.Collection/HealthReporter/HealthReporter.cs` | 成功/失败点数→一次健康信号，异常吞掉 |
| 熔断器 | `src/NitroGateway.Collection/Resilience/CircuitBreaker.cs` | Closed/Open/HalfOpen 状态机，冷却翻倍 5s→5min，单探测 |
| 熔断注册表 | `src/NitroGateway.Collection/Resilience/CircuitBreakerRegistry.cs` | 按设备惰性创建/复用熔断器 |
| 熔断监听 | `src/NitroGateway.Collection/Resilience/CircuitBreakerHealthListener.cs` | Online→Reset，Offline→Trip |
| DI 注册 | `src/NitroGateway.Collection/CollectionServiceCollectionExtensions.cs` | Singleton/Scoped 生命周期约定 + 参数默认值 |

## 跨模块依赖（答题时需要知道的上下文）

- `IDeviceManager.GetAllAsync()`：Device 模块，返回**全部设备（含 Offline/Error）**
- `IDeviceHealthMonitor`：Device 模块，健康判定的唯一决策者（默认连续失败 3 次→Offline，连续成功 3 次→Online）
- `IProtocolDriverPool` / `ReliableProtocolDriver`：Protocol 模块，长连接复用与断线自愈
- `IForwardBuffer`：Storage.Buffer，转发缓冲（MQTT 转发数据源）
- `IMeasurementStore`：Storage.TimeSeries，时序库（SQLite 实现）
- `GatewayLifecycle`：Host 模块，采集→转发 drain 协调标志

## 注意事项

- **代码是唯一事实来源**。`DESIGN.md` / 子模块 `README.md` 存在文档漂移（例如「每轮连接/断开」「DispatchAsync 失败返回 Error」），答题以代码 + XML 注释为准，题目中也埋了漂移题。
- 测试是理解行为最快的捷径：`tests/NitroGateway.UnitTests`（CircuitBreakerTests / PointValuePipelineTests / DataDispatcherTests / DeviceCollectorMaintenanceTests）、`tests/NitroGateway.IntegrationTests`（CollectionEngineTests / PipelineDispatchIntegrationTests）。
- 答完所有题目后，试着不看代码把「采集一轮 + 设备故障 + 自愈恢复」的完整时序画出来——能画出来就是吃透了。
