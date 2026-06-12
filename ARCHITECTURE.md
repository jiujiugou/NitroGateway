# NitroGateway 模块说明

## 项目概述

NitroGateway 是一个工业协议网关，基于 .NET 技术栈，支持多协议接入与端云协同部署。

## 依赖层级

```
Layer 0（无依赖）    Domain, Shared
Layer 1（基础设施）  Transport, Storage
Layer 2（领域服务）  Protocol, Device, Collection, Alarm, Forwarder, Scheduler
Layer 3（应用层）    Webapi, Admin
```

## 模块一览

| 模块 | 层级 | 职责 |
|---|---|---|
| **Domain** | Layer 0 | 核心领域模型：设备实体、测点值、告警规则、协议驱动接口等 |
| **Shared** | Layer 0 | 横切关注点：日志、依赖注入、配置管理、通用工具类 |
| **Transport** | Layer 1 | 传输层封装：MQTT 客户端、HTTP/gRPC 客户端 |
| **Storage** | Layer 1 | 持久化层：配置存储、时序数据存储、离线缓冲 |
| **Protocol** | Layer 2 | 多协议适配：Modbus、OPC UA、Siemens S7 等驱动实现 |
| **Device** | Layer 2 | 设备管理：设备注册、配置、状态监控与生命周期管理 |
| **Collection** | Layer 2 | 采集引擎：轮询调度、超时处理、失败重试、数据分发 |
| **Alarm** | Layer 2 | 告警引擎：规则评估、告警触发与恢复、通知推送 |
| **Forwarder** | Layer 2 | 数据转发：边缘上行、指令下行、断点续传 |
| **Scheduler** | Layer 2 | 通用任务调度：为采集、告警、转发提供定时触发能力 |
| **Webapi** | Layer 3 | REST API 网关：对外暴露 HTTP 接口 |
| **Admin** | Layer 3 | 管理后台：可视化的设备管理与系统监控 |
