# NitroGateway 演示指南

## 环境准备

```bash
# 1. 启动 MQTT Broker
docker compose up -d

# 2. 启动你的 Modbus 模拟软件（任意标准 Modbus TCP 模拟器均可）
# 暴露 :502，配置 10 个 Holding Register

# 3. 启动网关
```

访问：
- 前端: http://localhost:5173
- 网关: http://localhost:5100
- 指标: http://localhost:5100/metrics
- 健康: http://localhost:5100/healthz

默认账号: admin / admin123

---

## 演示场景（9 步，约 8 分钟）

### 场景 1：注册设备 + 看到数据采集（2 分钟）

```
操作:
  1. 浏览器打开 http://localhost:5173 → 登录 admin/admin123
  2. 设备管理 → 新增设备
     名称: Demo PLC
     协议: Modbus TCP
     地址: 127.0.0.1:502
  3. 进入设备详情 → 点位管理 → 添加 4 个点位:
     Temp    | 40001 | Float  | Scale=1.0
     Press   | 40003 | Int16  | Scale=1.0
     Status  | 40004 | Int16  | Scale=1.0
     Level   | 40005 | Float  | Scale=1.0
  4. 等待 1 秒（采集周期），刷新页面

预期:
  4 个点位的数据在仪表盘实时更新
  时序数据库有一条记录

验证:
  curl http://localhost:5100/api/measurements/history?deviceId=xxx&pointId=xxx
  curl http://localhost:5100/metrics | grep nitro_collection_total
```

### 场景 2：CSV 批量导入/导出（30 秒）

```
操作:
  1. 点位管理 → 导出 CSV → 浏览器下载文件
  2. 用 Excel 打开 CSV，修改 Scale 列，保存
  3. 导入 CSV → 选择修改后的文件

预期:
  点位 Scale 全部更新

验证:
  导出的 CSV 包含正确的列头和行数
```

### 场景 3：批量生成 100 个点位（30 秒）

```
操作:
  点位管理 → 批量生成
    名称模板: AI_{###}
    起始地址: 40010
    数量: 100
    数据类型: Float

预期:
  生成 AI_001@40010, AI_002@40012, ... AI_100@40208
  Float 步长 = 2，地址正确递增
```

### 场景 4：熔断器演示（1 分钟）

```
操作:
  1. 开新终端 → 停掉模拟 PLC (Ctrl+C)
  2. 观察 dotnet run 输出 → 连续失败
  3. 等待 5 次失败后 → 日志显示 "CircuitBreaker 已断开"
  4. 指标: nitro_circuit_breaker_state{device="xxx"} = 1 (Open)
  5. 重新启动模拟 PLC → 30s 后自动 HalfOpen → 探测成功 → Closed

预期:
  PLC 离线期间，该系统设备的采集被跳过（不阻塞其他设备）
  恢复后自动检测并恢复采集

验证:
  观察 metrics 中 nitro_circuit_breaker_state 从 0→1→2→0 的变化
```

### 场景 5：转发节流演示（1 分钟）

```
操作:
  1. docker compose stop mqtt                    # 模拟 Broker 宕机
  2. 等待 10 秒 → 日志 "转发失败"
  3. 观察 metrics: nitro_throttle_batch_size 从 1000 → 500 → 250 → 125 → 100
  4. docker compose start mqtt                   # Broker 恢复
  5. 观察 metrics: 批量大小从 100 逐步恢复 → 500 → 1000

预期:
  MQTT 断连时节流生效，恢复时不冲垮 Broker
```

### 场景 6：死信队列 + 管理系统状态（30 秒）

```
操作:
  curl http://localhost:5100/api/status/system

预期:
  BufferBacklog: 积压批次数量
  MqttConnected: true/false

  curl http://localhost:5100/api/status/devices/health
  → 设备健康快照: LastCollectionAt, ConsecutiveFailures...
```

### 场景 7：安全 — RBAC + 审计（30 秒）

```
操作:
  1. 退出登录 → 用 operator/oper123 登录
  2. 尝试删除设备 → 被拒绝 (403)
  3. 尝试导出 CSV → 通过（Operator 有权限）
  4. 查看日志 → 搜索 "AUDIT" → 看到所有 API 调用记录

验证:
  不同角色权限差异
  Serilog 输出的 AUDIT 日志包含 User + Method + Path + Status + IP
```

### 场景 8：健康检查（10 秒）

```
操作:
  curl http://localhost:5100/healthz    # { "status": "Healthy" }
  curl http://localhost:5100/readyz     # SQLite + MQTT 检查
```

---

## 已知限制（诚实面对）

| 限制 | 说明 |
|---|---|
| 未连接真实 PLC | 使用标准 Modbus TCP 模拟器代替，协议帧完全相同 |
| 告警重启丢失 | `InMemoryAlarmRepository`，未持久化到 SQLite |
| OPC UA 未完成 | `OpcUaDriver` 骨架已实现，`ConnectAsync` 需对真实服务器调通 |
| 不含单元测试外的集成测试 | `SqliteForwardBuffer` DLQ 全流程需 2-3 个集成测试覆盖 |
| 单进程部署 | 无集群、无 Leader Election |

---

## Docker Compose 快速启动（无需真实硬件）

```yaml
# 已有环境:
#   - Mosquitto MQTT Broker (docker compose)
#   - Modbus 模拟器 (Python pymodbus)
#   - NitroGateway (.NET 10)
#   - Vue 3 前端 (Vite)

# 总耗时: ~3 分钟从零到看到实时数据
```
