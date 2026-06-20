---
name: industrial-learning-resources
description: 工业协议网关学习资料汇总
metadata:
  type: reference
---

## 工具（免费，先装这些）

| 工具 | 干什么 | 链接 |
|---|---|---|
| ModbusPal | Modbus TCP 模拟器，定义寄存器/线圈，驱动读它 | https://sourceforge.net/projects/modbuspal/ |
| Prosys OPC UA Simulation Server | OPC UA 模拟服务器，免费版够用 | https://www.prosysopc.com/products/opc-ua-simulation-server/ |
| Codesys | 免费软 PLC，写梯形图/ST 语言，模拟真实 PLC | https://www.codesys.com/ |
| Wireshark | 抓 Modbus TCP 帧看原始报文 | https://www.wireshark.org/ |
| MQTTX | MQTT 客户端，调试发布/订阅 | https://mqttx.app/ |

## Modbus

- **Modbus 协议规范（中文）** — 搜"Modbus协议中文版 中国工控网"，一页 PDF 讲清所有功能码
- **Simply Modbus** (www.simplymodbus.ca/FAQ.htm) — 最简洁的 Modbus FAQ，寄存器/功能码/出错码一张表
- **ModbusPal 教程** — YouTube 搜 "ModbusPal tutorial"，5分钟学会模拟

## OPC UA

- **OPC Foundation 官方文档** — https://reference.opcfoundation.org/ 选 "Part 1 Overview"
- **Prosys 官方教程** — Prosys 安装后自带 Quick Start，连上就能浏览 NodeId 树

## S7 (西门子)

- **S7netplus GitHub** — https://github.com/S7NetPlus/s7netplus README 有地址格式说明 (DB1.DBD0 是什么意思)
- **"s7-1200 datasheet"** — 搜一下就知道 DB 块是什么、为什么会占 DBD0/DBW2 这种地址

## 工业网关开源参考

| 项目 | 语言 | 参考点 |
|---|---|---|
| HiveMQ Edge | Java | 工业协议网关产品级开源，看架构 |
| Neuron (EMQX) | C | 轻量工业协议网关，看驱动模式 |
| IoTGateway (GitHub) | C# | 国内 .NET 工业网关，看点位表设计 |
| FluentModbus | C# | 你在用的 Modbus 库，源码有完整示例 |

## 快速上手路径（按顺序）

1. 装 ModbusPal → 定义 3 个 HoldingRegister → 跑 ModbusDriver.ReadAsync → 拿到 ushort[]
2. 装 MQTTX → 连 test.mosquitto.org → 写段代码 Publish 刚才读到的值
3. 装 Prosys OPC UA → 看它的 NodeId 树 → 理解 ns=3;s=Temperature 是什么
4. 回头看自己的 Domain/Protocols/IProtocolDriver，就知道为什么这样设计了
