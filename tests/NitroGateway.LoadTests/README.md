# NitroGateway.LoadTests（L2 采集链路进程内压测）

用**假 `IDeviceReader`** 绕开协议层，把数据直接灌进**真实的**采集链路：

```
FakeDeviceReader → DeviceCollector → PointValuePipeline → DataDispatcher → MeasurementWriteHost → Store
```

只压网关自身（CPU / 有界 Channel / SQLite 写），排除 Modbus 模拟器与网络噪声。

## 运行

```powershell
# Mode A：假 Store，测 Pipeline + Channel 消费能力，找丢弃拐点
dotnet run -c Release --project tests/NitroGateway.LoadTests -- `
  --mode A --devices 50,100 --points 50 --concurrency 5,20,100 --seconds 20

# Mode B：真实 SQLite（临时库 + 自动迁移），测写天花板
dotnet run -c Release --project tests/NitroGateway.LoadTests -- `
  --mode B --devices 20,50 --points 50 --concurrency 5,20 --seconds 20 --report load-report.md
```

参数（`--help` 亦可见）：

| 参数 | 说明 | 默认 |
|------|------|------|
| `--mode A\|B` | A=假 Store；B=真实 SQLite | A |
| `--devices` | 设备数扫描列表（逗号分隔） | 50 |
| `--points` | 每设备点位数 | 50 |
| `--concurrency` | 单轮并发扫描列表（映射 `Collection:MaxConcurrency`） | 5,20,100 |
| `--seconds` | 每场景测量时长 | 20 |
| `--warmup` | 预热时长 | 3 |
| `--interval-ms` | 轮间隔；0=背靠背全速找饱和 | 0 |
| `--report` | 输出 Markdown 报告路径 | 无 |

## 判定口径

- **丢弃数 = 假 Reader 产出点数 − 落库点数**（测量结束后等两个有界 Channel 排空再统计）。
  两个 Channel 都是 `BoundedChannelFullMode.DropOldest`，满时静默丢最旧——这是本压测最关心的信号。
- **拐点**：`丢弃 > 0` 或 `p99 逼近采集周期` 的那个设备数/并发档位。
- 每次运行前建议记录基线（首行日志含空库状态下的指标），并保证单独一台机器运行。

## 非目标

- 协议驱动、驱动池、熔断器（用假 Reader 直接绕过）。
- MQTT 转发、网络、模拟器容量——见 L3（`ModbusSlaveSim` + 本地 `dotnet run` 网关）。
- 未改生产代码：丢弃数靠对账得出，未加实时埋点。

## L3 配套

多从站 CSV 生成器：

```powershell
pwsh -File tools/factory-test/gen-points-csv.ps1 -OutDir D:\tmp\load-csv -SlaveCount 50 -PointsPerSlave 50
```

再用 `ModbusSlaveSim --csv-dir D:\tmp\load-csv --port 15020 --tick 1000` 起模拟器，
网关设备指向 `127.0.0.1:15020`、UnitId=1..N 做端到端压测。
