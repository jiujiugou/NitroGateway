# NitroGateway 工厂环境模拟检测指南

## 检测环境准备

| 组件 | 工具 |
|---|---|
| Modbus 从站模拟 | Modbus Slave / ModRSsim2 等，至少1台，端口 :502 |
| MQTT Broker | Mosquitto (docker或Windows版), 端口 :1883 |
| 网络工具 | clumsy (Windows) / tc (Linux) — 模拟延迟/丢包 |
| 后端 | `dotnet run --project src/NitroGateway.Webapi` |
| 前端 | `cd web && npm run dev`，浏览器 F12 Console |
| 监控 | `http://localhost:5100/metrics` + `http://localhost:5100/healthz` |

---

## 一、基础连通性检测 (10 分钟)

| # | 检测项 | 操作 | 验收标准 |
|---|---|---|---|
| 1.1 | 启动顺序 | ① 启动 Modbus 从站 ② 启动 MQTT ③ 启动网关 | 网关日志无异常，/healthz 返回 Healthy |
| 1.2 | 设备注册 | 注册设备: Modbus/TCP → 127.0.0.1:502 | 设备状态为 Online |
| 1.3 | 点位采集 | 添加点位 Temp@40001(Float) | 3 秒后 `/metrics` 出现 `nitro_collection_total{status="success"}` |
| 1.4 | 数据入库 | `GET /api/measurements/history` | 返回采集数据 |
| 1.5 | MQTT 状态 | `/metrics` 查询 `nitro_mqtt_state` | 值为 2 (Connected) |
| 1.6 | 前端访问 | 浏览器打开仪表盘+系统状态 | MQTT 显示已连接，设备显示 Online，数据在刷新 |

---

## 二、网络异常检测 (30 分钟)

### 2.1 短时断连 (< 30 秒)

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 正常运行中，断开 Modbus 从站连接 (关闭模拟器或 kill 进程) | — |
| 2 | 等待 10 秒 | CB 变为 Open (指标 `nitro_circuit_breaker_state` = 1) |
| 3 | 等待期间观察 | 日志中不再出现 "开始读取设备"，采集轮次跳过该设备 |
| 4 | 重启 Modbus 从站 | — |
| 5 | 等待 35 秒 | CB 自动恢复 (Closed)，设备恢复 Online |
| 6 | 验证 | `/metrics` 中 `nitro_circuit_breaker_state` 回到 0 |

### 2.2 长时断连 (> 5 分钟)

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 断开 Modbus 从站 | CB 打开，冷却时间 30s |
| 2 | 保持断开 5 分钟 | CB 每次探测失败，冷却时间翻倍直到 5min 上限 |
| 3 | 恢复连接 | CB 半开探测成功，恢复 Closed |
| 4 | 验证 | 设备自动从 Offline → Online，全程无需人工干预 |

### 2.3 网络质量差 (延迟/丢包)

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 用 clumsy 给 :502 加 200ms 延迟 + 5% 丢包 | — |
| 2 | 观察采集 | `nitro_collection_duration_ms` 升高但不崩溃 |
| 3 | 观察日志 | 部分采集失败，重试成功或跳过 |
| 4 | 取消限流 | 恢复正常采集 |

---

## 三、MQTT Broker 异常检测 (20 分钟)

### 3.1 Broker 短时中断

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 网关正常运行 | MQTT Connected |
| 2 | `docker stop mqtt` 或 `taskkill mosquitto` | MQTT State → Disconnected |
| 3 | 等待 10 秒 | 转发出现 "MQTT 未连接" Warning，数据仍在 ForwardBuffer |
| 4 | `docker start mqtt` 或重启 mosquitto | MQTT 自动重连 → Connected |
| 5 | 验证 | 之前积压的数据被转发，ForwardBuffer 清空 |

### 3.2 Broker 反复抖动

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 反复 `docker restart mqtt` 5 次，间隔 5 秒 | 每次自动重连成功 |
| 2 | 观察 ForwardingThrottle | `/metrics` 中 `nitro_throttle_batch_size` 在 100-1000 之间自适应变化 |
| 3 | 观察死信 | `/api/deadletters` 为空（短暂断连不触发死信） |

### 3.3 Broker 长时间不可用

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 关掉 Broker 15 分钟 | Fail → retry_count 累加 |
| 2 | 某些批次 retry_count ≥ 5 | 状态变为 DeadLetter |
| 3 | 检查 | `/api/deadletters` 列出死信条目 |
| 4 | 重放死信 | `POST /api/deadletters/{id}/retry` → 重新转发成功 |

---

## 四、进程异常检测 (15 分钟)

### 4.1 网关进程异常退出 (模拟断电)

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 网关正常采集 1 分钟后 | — |
| 2 | `taskkill /F /IM dotnet.exe` (Windows) 或 kill -9 | 进程立即退出 |
| 3 | 重启网关 | — |
| 4 | 验证 SQLite | 数据文件未损坏，可正常读取 |
| 5 | 验证 ForwardBuffer | InFlight 批次退回 Pending (不会丢) |
| 6 | 验证设备 | 设备状态重新判定 (从 Unknown 开始) |

### 4.2 数据库文件被误删

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 删除 `nitrogateway.db` | — |
| 2 | 重启网关 | FluentMigrator 自动建表 |
| 3 | 重新注册设备 | 正常采集 |
| 4 | 验证 | 新数据正常写入 SQLite |

---

## 五、配置变更检测 (15 分钟)

### 5.1 运行时修改设备配置

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 网关正常采集 | — |
| 2 | API 修改设备 IP (改为无效地址) | 当前采集轮继续，下一轮失败 |
| 3 | API 改回正确 IP | 下一轮恢复采集 |
| 4 | 观察日志 | 整个过程无异常、无崩溃 |

### 5.2 运行时增删点位

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | API 添加 10 个点位 | 下一轮开始采集新点位 |
| 2 | API 删除 5 个点位 | 下一轮不再采集已删除的点位 |
| 3 | API 批量生成 500 个点位 | 生成成功，数据量增加但不影响现有采集 |

### 5.3 CSV 导入导出

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | GET `/api/devices/{id}/points/export` | 下载 CSV，列头正确 |
| 2 | 在 Excel 中修改 Scale 列 | — |
| 3 | POST `/api/devices/{id}/points/import` | 导入成功，Scale 值更新 |

---

## 六、长时间运行检测 (8 小时)

### 6.1 8 小时长稳

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 1 台设备 + 10 点位 + 1s 采集周期 | 启动并记录初始状态 |
| 2 | 运行 8 小时 | — |
| 3 | 检查进程 | 进程仍在运行 |
| 4 | 检查内存 | `dotnet-counters` GC Heap Size 不大于初始的 2 倍 |
| 5 | 检查 SQLite | 文件大小合理增长 (约 10-50MB) |
| 6 | 检查日志文件 | `logs/` 目录仅保留最近 7 天的文件 |

### 6.2 磁盘空间

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 运行后检查 | SQLite < 200MB (非高频写入场景) |
| 2 | 日志轮转 | 旧日志文件被自动清理 |

---

## 七、多设备并发检测 (10 分钟)

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | 模拟器开 10 个 Modbus 从站，每个 50 点位 | — |
| 2 | 注册全部 10 台设备 | 全部 Online |
| 3 | 观察采集顺序 | 最多 5 台同时采集 (SemaphoreSlim(5) 限流) |
| 4 | 关掉其中 3 台 | 对应 CB 打开，其余 7 台不受影响 |
| 5 | 恢复 3 台 | 对应 CB 自动恢复 |

---

## 八、权限与安全检测 (10 分钟)

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | viewer 登录，尝试删除设备 | 403 |
| 2 | operator 登录，尝试确认告警 | 200 |
| 3 | 不带 Token 访问 `/api/devices` | 401 |
| 4 | 检查 Serilog 日志 | 所有操作都有 AUDIT 记录 |
| 5 | 检查 SignalR 连接 | 需要 Token 才能建立 WebSocket |

---

## 九、前端页面验收 (10 分钟)

| 页面 | 检测项 | 验收 |
|---|---|---|
| 登录 | admin/admin123 能登录 | [ ] |
| 仪表盘 | 设备统计正确 | [ ] |
| 设备管理 | CRUD 操作正常 | [ ] |
| 点位管理 | CSV 导入导出 + 批量生成 | [ ] |
| 实时监控 | 选设备后数据刷新 | [ ] |
| 历史数据 | 按时间范围查询 | [ ] |
| 系统状态 | MQTT/熔断器/设备健康状态正确 | [ ] |
| 告警管理 | (需先配告警规则) | [ ] |
| 死信管理 | 列表/重放/丢弃 | [ ] |
| Swagger | `http://localhost:5100/swagger` 可访问 | [ ] |

---

## 十、最终验收清单

| # | 场景 | 状态 |
|---|---|---|
| 1 | 正常采集 → SQLite 有数据 | [ ] |
| 2 | 断网 30s → 设备自动 Offline → 恢复后自动 Online | [ ] |
| 3 | MQTT 断连 → 节流生效 → 重连恢复 | [ ] |
| 4 | 网关进程 kill → 重启后数据不丢 | [ ] |
| 5 | 运行时改配置 → 不中断采集 | [ ] |
| 6 | 8 小时长稳 → 无内存泄漏 | [ ] |
| 7 | 10 设备并发 → 独立 CB 不互相影响 | [ ] |
| 8 | RBAC 权限隔离 | [ ] |
| 9 | 前端 10 个页面均可访问 | [ ] |
| 10 | `/healthz` 始终 200 | [ ] |

---

## 最关键的 3 个场景 (面试演示用)

**场景 1 — 断连自动化恢复 (2 分钟)**

关掉 Modbus 模拟器 → 看网关日志中 CB 三态变化 → 看前端设备状态变 Offline → 重启模拟器 → 等恢复 → 看前端变 Online。全程无人工操作。

**场景 2 — 数据不丢 (1 分钟)**

网关采集一会 → Kill 进程 → 重启 → 查 SQLite 数据完整 → 查 ForwardBuffer 无丢失。

**场景 3 — MQTT 节流 (1 分钟)**

关 MQTT Broker → 看 ForwardingThrottle 批量从 1000 降到 100 → 开 Broker → 看 Throttle 自动恢复到 1000。
