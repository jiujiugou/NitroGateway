# Device 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Device`（设备管理模块）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/并发/失败路径）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
IDeviceManager（设备生命周期）→ IDeviceHealthMonitor（健康判定/SST）
→ IDeviceSnapshotCache（目录缓存）→ IPointManager（点位管理）
→ PointBatchService（批量/CSV）→ Events + Listeners（事件回路）
→ DeviceServiceCollectionExtensions（DI 生命周期）→ 跨模块联动 → 开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 生命周期管理 | `src/NitroGateway.Device/DeviceManager.cs` | 设备注册/注销/查询/状态变更唯一入口；驱动驱逐 + 缓存失效联动 |
| 生命周期接口 | `src/NitroGateway.Device/IDeviceManager.cs` | 接口契约；`Status` 唯一入口约束的声明处 |
| 健康判定 | `src/NitroGateway.Device/DeviceHealthMonitor.cs` | 唯一 SST：连续失败/成功计数，阈值触发 Online/Offline 事件 |
| 健康快照 | `src/NitroGateway.Device/DeviceHealthSnapshot.cs` | 不可变 record，运维面板/告警查询的实时状态来源 |
| 目录缓存 | `src/NitroGateway.Device/DeviceSnapshotCache.cs` | 设备+点位配置内存缓存；SemaphoreSlim 双检 + TTL 兜底 |
| 点位管理 | `src/NitroGateway.Device/PointManager.cs` | 点位 CRUD/导入/校验；配置写入后失效缓存 |
| 批量服务 | `src/NitroGateway.Device/PointBatchService.cs` | CSV 导入导出、地址自动递增、名称模板占位符 |
| 持久化监听 | `src/NitroGateway.Device/Listeners/PersistenceListener.cs` | 健康事件 → 回调 `UpdateStatusAsync` 落库 |
| 监听注册 | `src/NitroGateway.Device/Listeners/HealthListenerRegistrar.cs` | IHostedService 启动时把 DI 中全部 listener 注册进 monitor |
| 状态事件 | `src/NitroGateway.Device/Events/DeviceHealthChanged.cs` | 状态迁移事件 record（OldStatus/NewStatus） |
| DI 注册 | `src/NitroGateway.Device/DeviceServiceCollectionExtensions.cs` | Singleton/Scoped 生命周期约定 + 健康阈值默认 3/3 |

## 跨模块依赖（答题时需要知道的上下文）

- `Domain.Devices`：`Device` / `DevicePoint` / `DeviceStatus` / `PointSnapshot`——配置模型与运行快照分离
- `Storage.Configuration`：`IDeviceRepository` / `IPointRepository`（纯接口，SQLite 实现）
- `Protocol.Abstractions`：`IAddressParser`（地址校验委托，**尚未接线**）、`IProtocolDriverPool`（驱动池驱逐）
- `Shared`：`OperationResult` / `OperationalError`（模块间错误传递的唯一载体）
- Collection 模块：`HealthReporter` 每轮上报成功/失败信号；熔断器与健康各自决策（链路 vs 质量）
- 测试：`DeviceManagerTests` / `DeviceHealthMonitorTests` / `PointManagerTests` / `PointBatchServiceTests` / `DeviceCollectorMaintenanceTests`（跨模块维护过滤）

## 注意事项

- **代码是唯一事实来源**。`DESIGN.md` 存在文档漂移（如 Q8.3 死区语义、Q6.5 地址生成边界），答题以代码 + XML 注释为准。
- 区分「代码强制」与「约定约束」：Status 唯一入口、缓存返回对象不得修改、listener 异常自隔离——都是接口注释/约定，代码层面没有强制（Q4.5、Q7.4 专门考这个）。
- 测试是理解行为最快的捷径：`tests/NitroGateway.UnitTests` 下四个 Device 相关测试文件覆盖了状态机、防抖、清理、批量导入等核心行为。
- 答完所有题目后，试着不看代码把「设备上线 → 连续 3 次失败离线 → 自愈恢复上线」的完整时序画出来（含 Collection/Protocol/DB 参与方）——能画出来就是吃透了。
