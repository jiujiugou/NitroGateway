# NitroGateway 出厂测试标准（FACTORY-TEST）

## 0. 轨道划分与判定总则

**两条轨道（放行时合并判定）**
- **A · 软件可实现（T0~T8）**：纯软件 / 回环 / 模拟器闭环，无需真实 PLC，每次发布可回归。
- **B · 必须真实硬件（H1~H4）**：必须接真实 PLC / 真实串口 / 目标边缘主机，出厂与现场验收执行；执行前先按 §12.1 登记被测硬件。

**分级与放行**
- P0 基线门禁（必须全过）/ P1 核心场景（必须全过）/ P2 辅助场景（可记录缺陷后置）
- 放行规则：
  - A 轨 P0+P1 全过 → 判定“可用、可长期运行”，软件放行。
  - B 轨按**出货组合**逐项：随货交付的协议/部署形态对应 P0 项必须通过 → 可发货；不随货项目在判定表标 N/A。
- 每次测试实测数据与结论记录到 `notes/worklog/YYYY-MM-DD.md`。

## 1. 测试环境与工具

### 1.1 软件模拟栈（A 轨）

| 组件 | 工具 | 要求 |
|---|---|---|
| Modbus 从站模拟 | ModbusSlaveSim（首选）/ Witte mbslave / pymodbus | ≥10 台，端口 :502，每台 50 点位（搭建见附录 A） |
| MQTT Broker | `eclipse-mosquitto:2`（docker compose） | 端口 :1883 |
| 网络扰动 | clumsy（Windows）/ tc（Linux） | 200ms 延迟 + 5% 丢包 |
| 后端 | `dotnet run --project src/NitroGateway.Webapi` | :5100 |
| 前端 | `cd web && npm run dev` | :5173，登录 admin/admin123 |
| 监控 | `/metrics` `/healthz` `/readyz` + `dotnet-counters` | 口径见 §2 |

**测试前置条件（保证基线干净）**
- 干净 SQLite：备份/删除 `src/NitroGateway.Webapi/nitrogateway.db*` 后重启，记录初始文件大小
- 确认无死信残留（死信特性已移除，启动会清理历史死信行）
- 记录基线：启动内存、backlog、SQLite 大小、`collection_total`

### 1.2 被测硬件（B 轨）

登记表见 §12.1。每台被测设备型号/参数/位置在测试前填入，B 轨每条记录关联该登记行。

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
| 转发丢弃 | `nitro_forward_total{status="dropped"}` | 常态为 0；仅重试超限时增长 |
| 内存 | `dotnet_total_memory_bytes` / dotnet-counters GC Heap | 8h 后 ≤ 初始 2 倍 |
| 磁盘 | SQLite 文件 + `logs/` 目录 | 8h 增量 ≤50MB；日志仅保留 7 天 |
| 落库失败 | `nitro_store_write_failures_total` | 全程不增长 |

## 3. T0 构建与启动基线（A 轨 · P0，10 分钟）

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T0.1 | 后端构建 | `dotnet build NitroGateway.slnx` | 0 错误 |
| T0.2 | 单元测试 | `dotnet test tests/NitroGateway.UnitTests` | ≥292 全通过 |
| T0.3 | 集成测试 | `dotnet test tests/NitroGateway.IntegrationTests` | ≥40 全通过 |
| T0.4 | 前端构建 | `cd web && npm run build` | vue-tsc 0 错误，vite 构建成功 |
| T0.5 | 启动 | 顺序启动 MQTT → 网关 | ≤15s 内 `/healthz` 200；`/readyz` Healthy；`nitro_mqtt_state=2` |
| T0.6 | 登录 | `POST /api/auth/login` admin/admin123 | 200 且返回 JWT |

## 4. T1 单设备端到端功能（A 轨 · P0，20 分钟）

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T1.1 | 设备注册 | 注册 Modbus/TCP → 127.0.0.1:502 | 设备 Online（≤2 轮采集内） |
| T1.2 | 点位采集 | 添加点位 Temp@40001(Float) | 3s 内 `nitro_collection_total{status="success"}` 增长 |
| T1.3 | 数据入库 | `GET /api/measurements/history` | 返回采集数据，值符合 WriteGuard 校验 |
| T1.4 | MQTT 转发 | 订阅 `nitrogateway/{deviceId}/measurements` | 收到与采集一致的数据 |
| T1.5 | 前端访问 | 仪表盘 + 系统状态页 | MQTT 已连接、设备 Online、数据刷新 |

## 5. T2 多设备并发（A 轨 · P0 核心，30 分钟）

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

## 6. T3 长时间运行 8 小时（A 轨 · P0 核心，8 小时）

前置：1 台设备 × 10 点位 × 1s 采集周期（或沿用 T2 环境），先记录基线指标。

| # | 标准 | 采样方式 | 通过标准 |
|---|---|---|---|
| T3.1 | 进程存活 | 每小时检查 | 全程无退出 |
| T3.2 | 健康检查 | 每小时 `GET /healthz` | 全程 200 |
| T3.3 | 内存稳定 | dotnet-counters GC Heap Size | 结束时 ≤ 初始 2 倍，曲线无持续上升（无泄漏） |
| T3.4 | 转发积压 | `nitro_buffer_backlog` | MQTT 在线期间 ≤5，不持续增长 |
| T3.5 | 转发丢弃 | `nitro_forward_total{status="dropped"}` | 不产生新丢弃 |
| T3.6 | SQLite 增长 | 文件大小 | 8h 增量 ≤50MB（10 点位 1s），总量 <200MB |
| T3.7 | 日志轮转 | `logs/` 目录 | 仅保留最近 7 天，不占满磁盘 |
| T3.8 | 数据连续性 | 抽查 measurements 时间戳 | 无大段空洞（断连期除外）；`nitro_store_write_failures_total` 不增长 |
| T3.9 | 重启恢复 | 8h 后 kill -9 再重启 | 数据不丢，日志出现 InFlight→Pending 恢复，补发完成 |

**判定**：T3.1~T3.9 全过 → 长时间运行达标。测试记录至少包含时间点、内存、backlog、SQLite 大小四列实测数据。

## 7. T4 故障恢复（A 轨 · P1，1.5 小时）

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
| T4.2.2 | 反复抖动 | `docker restart mqtt` ×5（间隔 5s） | 每次自动重连成功，不产生丢弃 |
| T4.2.3 | 长断 15min | 关闭 broker 15 分钟 | 超重试上限的批次被丢弃（不再产生死信）；恢复后可正常续传 |

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

## 8. T5 安全与权限（A 轨 · P1，10 分钟）

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T5.1 | 越权写 | viewer 登录尝试删除设备 | 403 |
| T5.2 | 授权写 | operator 登录确认告警 | 200 |
| T5.3 | 未认证 | 无 Token 访问 `/api/devices` | 401 |
| T5.4 | 审计 | 检查 Serilog 日志 | 所有 `/api/*` 操作有 AUDIT 记录 |
| T5.5 | SignalR | 无 Token 建连 | 拒绝建立 WebSocket |

## 9. T6 前端验收（A 轨 · P2，10 分钟）

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
| Swagger | `/swagger` | 可访问 |

## 10. T7 中心形态端到端（A 轨 · 现场→中心，P1，40 分钟）

前置: 启动中心栈 `docker compose -f docker-compose.center.yml up -d --build`（mqtt + ingest + 中心 gateway + web）；
现场端二选一——Windows 桌面端（`src/NitroGateway.Desktop`）或另一台机器/容器跑网关；Modbus 从站模拟器 ≥1 台。
验证「现场 → broker → ingest → 中心库 → 中心 Web」全链路。

| # | 标准 | 操作 | 通过标准 |
|---|---|---|---|
| T7.1 | 中心栈就绪 | 顺序启动 mqtt → ingest → 中心 gateway → web | 三服务 `/healthz` 200；ingest `/readyz` Healthy 且 `nitro_mqtt_state=2` |
| T7.2 | 现场采集上行 | 现场端注册 Modbus 从站并正常采集 | 现场端本地库有数据，MQTT 发布 `nitrogateway/{deviceId}/measurements` 成功 |
| T7.3 | 中心入库 | 观察 ingest 日志与指标 | `nitro_ingest_received_total{kind="measurements"}` 持续增长；日志出现「遥测入库: 批次 ... 新增 N」 |
| T7.4 | 中心库有数 | 中心 API `GET /api/measurements/history`（:5100） | 返回与现场采集一致的数据（值/时间戳/点位名） |
| T7.5 | 中心展示 | 浏览器 `http://localhost:5170` 登录 admin/admin123 | 仪表盘与历史曲线出现现场数据 |
| T7.6 | 幂等去重 | 现场端重复投递（重启现场端或重发批次） | `nitro_ingest_dedup_total` 增长，中心库行数不重复（记录主键幂等） |
| T7.7 | 断网续传 | 停现场端 MQTT 一段时间后恢复 | 现场 `forward_buffer` 排队不丢，恢复后补发清空；中心库最终一致 |
| T7.8 | 中心重启 | `docker compose -f docker-compose.center.yml restart ingest` | 重启后继续入库，迁移幂等不报错，中心库不丢不重复 |

**判定**：T7.1~T7.8 全过 → 中心形态可用。

## 11. T8 软件补齐门禁（A 轨 · P1/P2，纯软件可闭环）

T0~T7 未覆盖、但纯软件即可验证的缺口。随发布回归一起跑。

| # | 标准 | 操作 | 通过标准 | 级别 |
|---|---|---|---|---|
| T8.1 | 数值/字节序闭环 | 用 hsl-probe 向设备写已知种子值（覆盖 9 种数据类型 + String）→ 网关采集 2 轮 → `GET /api/measurements/history` 读回比对；对 32/64 位多字点改 `DataFormat=CDAB` 复测 | 读回值与种子一致、高低字顺序正确；类型转换/落库失败日志 0；`nitro_store_write_failures_total` 不增长 | P1 |
| T8.2 | 缩放/死区/降频 | 给点位配工程缩放（raw×Scale）、死区阈值、降频 → 模拟器按已知值序列步进 | 库内值 = raw×Scale；死区内变化不落新行；降频后记录间隔符合配置 | P1 |
| T8.3 | 命令回写 + 幂等 | `POST /api/write` 写 00001 线圈与 40001(Float) → 模拟器读回确认；同一 commandId 重复提交 2 次；viewer 写 → 403；WriteGuard 超范围/超变化率写；查 `commands/ack` 回执 | 设备值 = 目标值；重复 commandId 仅生效一次；越权 403；越界写被拒且设备值不变；ack 均到达 | P1 |
| T8.4 | 告警端到端 | 建规则（Temp>80, Duration 10s）→ 模拟器拉高值持续 >10s → `GET /api/alarms`；确认；值回落 | 状态机 Active→Acknowledged→Resolved 正确、Active 期间不重复生成；Notification 开启时有 MQTT 告警消息 | P1 |
| T8.5 | OPC UA 纯软件对测 | 起外部 OPC UA 仿真服务器（能推 DataChange，如 UA Simulation Server / Prosys，工具未内置需自备）→ 配 OPC UA 设备读/写/Browse；服务器侧改值；断服务器再恢复 | 读回一致；订阅推送入库（非轮询）；断线→CB Open→恢复后自动 Online；Browse 出地址空间 | P1 |
| T8.6 | 版本升级迁移 | 用上一发布版本的 SQLite 备份启动当前版本 | FluentMigrator 迁移成功无异常；设备/点位/历史保留；采集继续正常 | P2 |

## 12. H 系列 · 必须真实硬件（B 轨）

适用：出货/现场验收。每条只对**随货交付的协议或部署形态**执行，不随货的在判定表标 N/A。
A 轨已用模拟器验证的**逻辑**不再重复；H 系列只验**真实线缆/真实设备/真实主机**上才成立的部分（时序、电气、厂商兼容、物理断连、平台基线）。

### 12.1 被测硬件登记表（执行前填写）

| # | 用途 | 设备/型号/固件 | 关键参数 | 位置/地址 | 备注 |
|---|---|---|---|---|---|
| A | 目标边缘主机 | | 型号 / OS / 磁盘 | | systemd / docker / Desktop 交付形态 |
| B | Modbus/TCP 从站 | | IP:端口 / 寄存器表 | | 真实 PLC/仪表，任意厂商 |
| C | Modbus RTU 总线 | | 串口号 / 波特率 / 校验 / ≥2 从站 UnitId | | USB-RS485 或原生 COM |
| D | S7 PLC | | CpuType / Rack / Slot | | 需 TIA/博途做真值对照 |
| E | OPC UA 端点 | | 端点 URL / 安全策略 / 证书 | | 带 UA 的控制器或真实 UA 服务器 |
| F | Mitsubishi FX/Q | | IP:端口 | | 驱动未启用时不测（§12.2） |
| G | 现场链路（可选） | | 交换机/网线/无线 | | H4 用 |

### 12.2 H1 协议真机对测矩阵

| # | 场景 | 操作 | 通过标准 | 级别 |
|---|---|---|---|---|
| H1-MB-RTU | Modbus RTU 物理串口采集 | 网关串口（原生 COM / USB-RS485）接 C 总线 ≥2 从站；注册 2 台设备（UnitId 1/2）× ≥10 点；采集参数与从站拨码一致 | 两站值正确（与现场仪表一致）；1s 周期无乱帧、CRC 错误计数不涨；拔从站 1 → 其 CB=Open 且从站 2 不中断；恢复 ≤35s Online；参数配错时稳定失败、不崩溃、日志可定位 | P0（RTU 出货） |
| H1-MB-TCP | 真实 Modbus/TCP 从站兼容抽查 | 对 B 真实设备注册采集 + 写值 | 读回与仪表/PLC 本体一致，无该型号特例（寄存器范围/异常响应） | P1 |
| H1-S7 | S7 真机采集 | 按 D 型号设 CpuType/Rack/Slot（与 TIA 工程一致）；DB（如 DB1.DBW0/DBD4）、M、I、Q 各 ≥2 点；与博途在线值对照；改错 Rack/Slot/CpuType 复测 | 各地址区读数正确、类型不错位；断电/停机→恢复后 ≤35s 自动回 Online | P0（S7 出货） |
| H1-UA | OPC UA 真实端点 | 连 E；证书双向信任（服务器证书入 `opcua/pki/trusted`）；Browse→配 NodeId；读/写；订阅 DataChange；断会话 | 安全连接成功；值正确；服务器侧改值推送到达；断线自动重建会话并恢复订阅 | P0（UA 出货） |
| H1-MEL | Mitsubishi MC 真机 | 仅当驱动重新启用并纳入 slnx 后执行：FX/Q 的 D/M/X/Y 读写 | 值正确、写生效 | 暂不执行（未启用） |

### 12.3 H2 命令写写真机闭环

| # | 场景 | 操作 | 通过标准 | 级别 |
|---|---|---|---|---|
| H2.1 | 真机回写生效 | 在 H1 已连通设备上 `POST /api/write` 写 Bool/数值到现场输出点（Q/线圈/D 寄存器，人工确认安全）→ 读回确认 | 现场值/动作正确、ack 到达；记录往返时延 | P0（回写随货出货） |
| H2.2 | WriteGuard 真机侧 | 超范围/超变化率写值 | 被拒且现场值未改变 | P0 |
| H2.3 | 越权写 | viewer 角色对真机写值 | 403，设备无动作 | P1 |

### 12.4 H3 目标主机长稳与性能基线

| # | 场景 | 操作 | 通过标准 | 级别 |
|---|---|---|---|---|
| H3.1 | 主机 8h 长稳 | 按交付形态（systemd+watchdog / docker / Desktop）部署到 A；接 H1 真机或 ≥10 从站模拟采集，跑 8h；采集 CPU/内存/GC/磁盘/SQLite 增量/MQTT 上行字节/采集耗时 P99/温度 | §2 健康口径全过；无进程退出；内存 ≤ 初始 2 倍；SQLite 增量 ≤50MB/8h | P0（每个交付主机型号一次） |
| H3.2 | 性能指纹基线 | 记录 H3.1 各项数值入库 | 形成该主机型号基线，供后续回归对照 | P1 |
| H3.3 | 主机断电重启 | 冷断电→重上电 | watchdog/systemd 自拉起，SQLite 完好，恢复采集无需人工 | P0 |

### 12.5 H4 现场链路/断电抗性

| # | 场景 | 操作 | 通过标准 | 级别 |
|---|---|---|---|---|
| H4.1 | 断 PLC 网线/串口 | 拔线→恢复 | 对应设备 CB=Open、其余不受影响；恢复 ≤35s Online；缓冲补发不丢 | P1 |
| H4.2 | 断现场交换机 | 关交换机→恢复 | MQTT/采集自动恢复、backlog 归零、无丢弃新增 | P1 |
| H4.3 | PLC 断电重启 | 现场设备掉电→上电 | 网关 ≤35s 自动回 Online，无人工 | P1 |
| H4.4 | 全链路停电冷启动 | 网关 + PLC + 交换机全断电→上电 | 各角色按序自拉起；数据无永久丢失（仅按超限策略丢弃）；SQLite 完好 | P1 |

## 13. 最终判定表（验收汇总）

| # | 标准场景 | 轨 | 级别 | 通过标准 | 结果 |
|---|---|---|---|---|---|
| 1 | 构建 + 测试 + 启动 | A | P0 | T0 全过 | [ ] |
| 2 | 单设备端到端 | A | P0 | T1 全过 | [ ] |
| 3 | 10 设备并发 + 隔离 | A | P0 | T2 全过 | [ ] |
| 4 | 8 小时长稳 | A | P0 | T3 全过 | [ ] |
| 5 | 断网 30s 自动恢复 | A | P1 | T4.1 全过 | [ ] |
| 6 | MQTT 断连节流补发 | A | P1 | T4.2 全过 | [ ] |
| 7 | 进程 kill 数据不丢 | A | P1 | T4.3.1 过 | [ ] |
| 8 | 运行时改配置不中断 | A | P1 | T4.4 全过 | [ ] |
| 9 | RBAC 权限隔离 | A | P1 | T5 全过 | [ ] |
| 10 | 前端页面可用 | A | P2 | T6 全过 | [ ] |
| 11 | 中心形态端到端 | A | P1 | T7 全过 | [ ] |
| 12 | 软件补齐（数值/回写/告警/UA/迁移） | A | P1 | T8.1~T8.5 全过 | [ ] |
| 13 | 协议真机对测 | B | P0 | 出货协议对应 H1 行全过 | [ ] |
| 14 | 写值回写真机闭环 | B | P0 | H2 全过（回写出货时） | [ ] |
| 15 | 目标主机长稳 + 基线 | B | P0 | H3 全过（交付主机型号） | [ ] |
| 16 | 现场链路/断电抗性 | B | P1 | H4 全过 | [ ] |

**放行结论**
- A 轨（1~12）P0+P1 全过 → 软件放行（可打包/试运行）。
- B 轨按出货组合：随货交付的 P0 项（13/14/15）通过，P1 项（16）按现场范围记录 → 可发货；不随货项目标 N/A。
- 任一不通过 → 记录缺陷到 `notes/ADR/` 后修复并回归。

## 14. 缺陷分级

| 级别 | 定义 | 示例 | 处理 |
|---|---|---|---|
| P0 | 阻断发布/数据丢失/崩溃 | 启动失败、采集数据丢失、进程崩溃 | 必须修复后放行 |
| P1 | 核心场景不达标 | 长稳内存超 2 倍、故障隔离失效、指标超阈值 | 必须修复 |
| P2 | 体验/文档类 | 前端样式、提示文案 | 可记录后置 |

## 附录 A · 模拟现场搭建（A 轨环境）

首选 `D:\resource\Modbus.project\ModbusSlaveSim`（CSV 驱动、单进程多从站多 IP）；10 个从站共用 `127.0.0.1:502`，靠 UnitId 1~10 区分。一键启动（自动读 `tools/factory-test/points-device-01~10.csv`，文件名 NN 即 UnitId）：

```powershell
cd D:\resource\Modbus.project
dotnet run --project ModbusSlaveSim -- --csv-dir D:\Code\NitroGateway\tools\factory-test
```

这正是产品 ModbusTCP 驱动设计的目标场景（同 IP:端口建多个设备、分别填 UnitId 1/2/3...）。
设计说明与 CSV 格式见 `D:\resource\Modbus.project\docs\ModbusSlaveSim-设计说明.md`。

### A.1 模拟器配置（每台从站 4 个 Function 块）

| 功能区 | 起始地址 | 数量 | 产品点位前缀 |
|---|---|---|---|
| 03 Holding Registers | 40001 | 90 | `4xxxx` |
| 04 Input Registers | 30001 | 6 | `3xxxx` |
| 01 Coils | 00001 | 2 | `0xxxx` |
| 02 Discrete Inputs | 10001 | 2 | `1xxxx` |

值默认按类型范围随机生成（`--tick <ms>` 周期刷新）；换点位/加从站 = 换 CSV 重启；断链演示：命令行输入 `d` 断开全部连接。

### A.2 产品侧设备注册参数

- 协议：Modbus / TCP；Endpoint：`127.0.0.1:502`
- 10 台设备分别填 `UnitId = 1..10`（即 Modbus Slave 的从站号）
- `DataFormat = ABCD`（默认，标准大端/高字在前）；读到 32/64 位乱码时换 `CDAB`（低字在前）

### A.3 点位加载（50 点/台 × 10 台，覆盖 9 种数据类型）

点位 CSV 已提交在 `tools/factory-test/points-device-01.csv` ~ `points-device-10.csv`：

- 保持寄存器 42 点：Float/Int32/UInt32/Int16/UInt16/Double/Int64/UInt64
- 输入寄存器 4 点、线圈 2 点、离散输入 2 点（Bool）
- 导入路径（桌面端，产品功能）：设备 → 点位管理 → `⬆ 导入 CSV`（桌面导入同 Web 共用 `PointBatchService` 解析器，格式一致）
- 自动化/脚本路径：`POST /api/devices/{id}/points/import`（Webapi 保留该 API，供集成测试与批量脚本）

### A.4 备用方案

- Witte Modbus Slave GUI COM 自动化：`tools/factory-test/mbslave-agent.ps1`（10 从站 × 4 Function 块，:502），`exp-10slaves.ps1`、`start-agent.ps1` 配套。
- 数值种子/读回校验：`tools/factory-test/hsl-probe`（HslCommunication ModbusTcpNet，10 从站多类型 seed + read-back）。
- Modbus RTU 软件闭环（非硬件，A 轨内部预检）：虚拟串口对（com0com/ELTIMA）跑 RTU 从站 ↔ 驱动，历史实测 37/37 通过。
