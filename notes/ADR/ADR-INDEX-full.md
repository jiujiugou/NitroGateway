# ADR Index — Full（L2）

> 本文件包含 Dependency Map 和按模块分类。日常查询用 `ADR-INDEX.md`（L1）。

---

## Dependency Map（演化链）

```text
架构演化:
  025 (B-Scheme) → 035 (角色分工) → 044 (Center裁剪) → 054 (Web=纯Edge)

OPC UA 四层:
  070 (Layer1 Browse) → 071 (Layer2 Subscription) → 072 (Layer3 Session) → 073 (Layer4 Security)

驱动并发:
  019 (驱动 _gate 串行) → 074 (单闸门所有权，装饰器去锁)
```

---

## By Module

| 模块 | ADR | 职责 |
|------|-----|------|
| architecture | 025, 026, 033, 035, 044, 054, 055, 069, 071, 072, 073 | 架构定义与演化 |
| security | 004 | 安全基线 |
| forwarder | 006 | MQTT 传输 |
| performance | 053 | 数据管道 |
| protocol | 070, 074 | OPC UA 封装 / 驱动串行化所有权 |
