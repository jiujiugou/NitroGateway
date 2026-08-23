# Persistence 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Persistence`（SQLite 实现层）与 `src/NitroGateway.Storage`（纯接口层）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/并发/失败路径）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
Storage 接口（只增不删）→ SqlitePragmas（并发基础）→ SqliteMeasurementStore（时序热路径）
→ SqliteForwardOutbox（两阶段/重试超限丢弃）→ MigrationRunner（迁移/备份）→ SqliteErrorClassifier（错误分类）
→ EF 仓储 + DomainMapper（配置 CRUD）→ MeasurementRetentionService（数据生命周期）→ 测试 → 开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 接口层 | `src/NitroGateway.Storage/{Configuration,TimeSeries,Buffer}` | 纯接口三组（设备/点位、时序、缓冲），零实现零重依赖 |
| DI 入口 | `src/NitroGateway.Persistence/Sqlite/SqliteServiceCollectionExtensions.cs` | Scoped EF 仓储 + Singleton Dapper 存储 + HostedService |
| 连接 PRAGMA | `src/NitroGateway.Persistence/Sqlite/SqlitePragmas.cs` | WAL + synchronous=NORMAL + busy_timeout=5000 |
| 时序存储 | `src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs` | Dapper 批量写入/范围查询/最新值/分页/清理 |
| 转发缓冲 | `src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs` | 两阶段 FIFO + 重试（超限丢弃）+ 启动恢复（2026-08-22 删死信） |
| 保留清理 | `src/NitroGateway.Persistence/Sqlite/MeasurementRetentionService.cs` | 后台周期删除过期时序数据 |
| 错误分类 | `src/NitroGateway.Persistence/Sqlite/SqliteErrorClassifier.cs` | SQLite 错误码 → OperationalError |
| EF 上下文 | `src/NitroGateway.Persistence/Sqlite/NitroGatewayDbContext.cs` | devices/points + alarms/alarm_rules 映射 |
| 设备/点位仓储 | `src/NitroGateway.Persistence/Sqlite/SqliteDeviceRepository.cs`、`SqlitePointRepository.cs` | EF upsert/级联/批量 |
| 告警仓储 | `src/NitroGateway.Persistence/Sqlite/SqliteAlarmRepository.cs`、`SqliteAlarmRuleRepository.cs` | EF + 统一异常分类 |
| 领域映射 | `src/NitroGateway.Persistence/DomainMapper.cs` | Domain ↔ EF 实体，参数 JSON 化 |
| 迁移执行 | `src/NitroGateway.Persistence/MigrationRunner.cs` | 备份 → MigrateUp → app_meta 版本 |
| 迁移脚本 | `src/NitroGateway.Persistence/Migrations/` | M001~M006 |

## 跨模块依赖（答题时需要知道的上下文）

- `IMeasurementStore`：Collection 的 `MeasurementWriteHost` 写入；Webapi 控制器查询（History / Latest / Paged）
- `IForwardBuffer`：Collection 入队；Forwarder 出队 / 提交 / 标记失败（死信方法【停用】保留，接口只增不删）
- `IDeviceRepository` / `IPointRepository`：Device 模块 `DeviceManager` / `PointManager` 消费
- `IAlarmRepository` / `IAlarmRuleRepository`：Alarm 模块消费（接口定义在 `NitroGateway.Alarm.Repository`）
- `OperationResult` / `OperationalError`：Shared 模块的返回值契约
- `GatewayActivitySource`：Telemetry 模块的 Activity 追踪

## 注意事项

- **代码是唯一事实来源**。Storage 的 README/DESIGN.md 存在文档漂移（例如「仓储 Singleton 注册」「`AddNitroSqlite(连接串)`」旧签名、「Infrastructure.Sqlite 目录」旧结构），答题以代码 + XML 注释为准，题目中也埋了漂移题。
- 测试是理解行为最快的捷径：`tests/NitroGateway.UnitTests`（SqliteMeasurementStoreTests / SqliteForwardOutboxTests / SqliteErrorClassifierTests / SqliteAlarmRepositoryTests / MeasurementRetentionServiceTests / MeasurementWriteHostTests）。
- 答完所有题目后，试着不看代码把「采集写入 → 转发出队 → 崩溃恢复 → 超限丢弃 → 迁移备份」的完整时序/状态机画出来——能画出来就是吃透了。
