# tools/script — 采集压力测试脚本

配合自研模拟器 **ModbusSlaveSim**（`D:\resource\Modbus.project\ModbusSlaveSim`）做多设备采集压测：
生成规模 → 起模拟器 → 注册设备并导入点位 → 跑网关 + 采样长稳。

## 文件

| 文件 | 作用 |
| --- | --- |
| `gen-load-points.ps1` | 生成 `points-device-NN.csv`（NN=UnitId），默认 50 从站 × 100 点位 |
| `register-devices.ps1` | 登录网关，逐台注册 Modbus/TCP 设备（`parameters.UnitId=NN`）并导入对应 CSV（**走 Webapi REST**） |
| `seed-desktop.ps1` + `seed-desktop.cs` | **桌面端（WPF，无 REST）专用**：直连其 SQLite 库写入设备+点位 |
| `points-device-NN.csv` | 模拟器与网关共用的点位定义（`Address/DataType/Enabled/MinValue/MaxValue`） |

## 快速开始（50 设备 × 100 点位）

```powershell
# 1) 生成配置（默认 50×100；-Clean 会先清旧文件）
pwsh tools/script/gen-load-points.ps1 -Clean

# 2) 起模拟器（端口与文件 NN 对应；50 个从站共用 127.0.0.1:15020）
#    默认 --profile realistic（拟真：过程量平滑漂移、累计量单调、开关低概率翻转）
dotnet run -c Release --project D:\resource\Modbus.project\ModbusSlaveSim -- `
  --csv-dir D:\Code\NitroGateway\tools\script --port 15020 --tick 1000
#    要“压满链路 / 击穿死区抑制”改写值策略：附 --profile stress

# 3) 起网关（本地 broker；默认 appsettings 指向公网 IP，必须覆盖）
$env:MQTT__Host='localhost'
dotnet run -c Release --project src/NitroGateway.Webapi

# 4) 批量注册 50 台设备 + 导入点位
pwsh tools/script/register-devices.ps1 -Base http://localhost:5100 -Endpoint 127.0.0.1:15020 -Count 50

# 5) 8h 长稳采样 + 判定
pwsh scripts/run-factory-tests.ps1 -T3Minutes 480 -T3DbPath src\NitroGateway.Webapi\nitrogateway.db
```

## 一键组合

```powershell
pwsh tools/script/gen-load-points.ps1 -Clean
pwsh tools/script/register-devices.ps1 -Count 50 -ReplaceExisting
```

## 桌面端（WPF，无 REST API）

桌面端进程内跑引擎、没有 HTTP 接口，改**直连其 SQLite 库**写入：

```powershell
# 1) 生成配置
pwsh tools/script/gen-load-points.ps1 -Clean

# 2) 起模拟器（桌面端设备默认指向 127.0.0.1:15020）
dotnet run -c Release --project D:\resource\Modbus.project\ModbusSlaveSim -- `
  --csv-dir D:\Code\NitroGateway\tools\script --port 15020 --tick 1000

# 3) 启动桌面端（WPF），等它建库（%LOCALAPPDATA%\NitroGateway\nitrogateway.db）

# 4) 灌 50 台设备 + 点位（无需重启；桌面端缓存 TTL=10s，约 10s 后自动加载）
pwsh tools/script/seed-desktop.ps1 -Count 50
#    已存在同名设备时默认跳过；要重灌点位：-ReplaceExisting
```

- 库路径默认 `%LOCALAPPDATA%\NitroGateway\nitrogateway.db`，可用 `-Db` 覆盖。
- 设备名 `LoadDev-001..050`，`UnitId` 写入 `ConnectionParams.UnitId`；`SiteId` 取桌面 `site.json`（`-SiteId` 可覆盖，否则沿用库中现有设备）。
- **关键**：EF Core SQLite 以**大写文本**存 Guid，脚本据此写 `Id`/`DeviceId`；写小写会导致桌面端“设备不存在”（点编辑/点位时按 Id 查不到）。
- 直连写库期间桌面端可能同时在写采集数据：脚本设 `busy_timeout=60s`、按设备分事务，冲突会等待而非报错。

## 规模阶梯

逐档跑短时（如 10 分钟），观察 `/metrics`、模拟器 CPU、网关 `nitro_collection_duration_ms`：

`10×50 → 30×50 → 50×50 → 50×100 → 100×100 …`

先出拐点的一方是瓶颈：
- 网关：`nitro_buffer_backlog` 上涨、`nitro_forward_total{status="dropped"}` 增长、采集耗时 p99 逼近周期。
- 模拟器：单 UnitId 请求被 `lock(store)` 串行；`--tick` 越小、点位越多，`Regenerate` 持锁越久。

## 注意

- **UnitId**：文件名 `points-device-NN.csv` 的 NN 即从站号；注册脚本写入 `connection.parameters.UnitId`。
- **地址布局**：生成器每 10 个点位放 1 个到输入寄存器（`3xxxx`，只读），其余保持寄存器（`4xxxx`），另加 2 线圈 + 2 离散。
- **死区抑制（ADR-053）**：生成器 CSV 无 `Deadband` 列 → 导入默认 `Deadband=0`，每个样本都落库，避免压测被抑制掩盖；要模拟真实死区请自行补列。
- **值生成档位**：模拟器默认 `--profile realistic`（拟真）；压测想制造更多变化用 `--profile stress`，或用 `--analog-step/--counter-inc/--bool-flip` 调参。
- **T3 的 50MB 阈值**：`scripts/factory/t3-longrun-sampler.ps1` 硬编码 `dbGrowthMb ≤ 50`（10 点位口径）。跑 50×100 这类最大量时 DB 必然超阈值，该项只作参考、别当门禁。
