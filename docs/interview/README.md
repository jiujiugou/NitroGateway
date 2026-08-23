# 面试准备总纲（岗位分层 → 复习重点 → 题库）

> 目标画像：**非应届（毕业 2 年、无相关开发经验）**，以 NitroGateway 完整作品集（.NET 10 工业物联网边缘网关 + Vue3/WPF 双端 + 726 单测 + Docker Compose 部署）应聘「经验不限 / 初级 / 应届可投」类岗位。
> 定位：初级岗位靠「可运行作品集 + 扎实基础」对冲年限；**不硬碰 3-5 年经验的资深 JD**。

**入口顺序：先读 `00-重点抓哪里与复习优先级.md`**（按项目拆的复习优先级 + 行动清单）→ 再按本总纲四节准备。

## 一、岗位分层与投递优先级（2026-08-20 核验）

| 优先级 | 岗位类型 | 代表 JD | 命中点 | 投递备注 |
|---|---|---|---|---|
| 1 | 物联网/数采开发 C#（经验不限） | 潍坊·芯知 9-14K：Modbus/OPC UA/Fins ≥1 + MQTT + Oracle/SQLServer | 多协议接入、MQTT 转发、采集引擎 | **最对口，重点投** |
| 2 | 上位机 C#（初级/不限经验） | 常州/嘉兴/中山：串口/TCP/Modbus、WPF MVVM | WPF 桌面端、Modbus 串口、多线程 | 双端作品差异化强 |
| 3 | 边缘网关/IIoT 全栈 | 南京 12-18K 应届可；台达 HVDC | 边缘计算、MES 集成、Vue+SQL | 薪资高，可争取 |
| 4 | .NET 初级研发/后端 | 学历不限、经验不限 4.5-8K | Web API、EF/Dapper、SQLite | 海投保底 |

## 二、面试点分级复习清单

### A 必考硬核（每场都问，先刷）
1. **C#/.NET 基础**：值类型/引用类型、string 不可变、ref/out、async/await 原理（状态机）、LINQ、GC、泛型、委托/事件
2. **多线程/异步**：Task vs Thread、async/await 死锁、锁（lock/Monitor/SemaphoreSlim）、并发集合、CancellationToken
3. **数据库**：SQLite WAL、EF Core vs Dapper、索引、批量写性能、事务、连接池
4. **ASP.NET Core**：中间件管道、依赖注入（生命周期）、JWT 认证/RBAC、SignalR 实时推送、配置/日志
5. **工业协议四件套（最大差异化）**
   - Modbus：功能码 01/03/04/05/06/0F/10、四类寄存器（线圈/离散/保持/输入）、RTU CRC、TCP 帧、批量读
   - MQTT：QoS 0/1/2、Retained 保留、Will 遗嘱、Keepalive、断线重连、主题
   - OPC UA：Client/Server、NodeId 四型、订阅 vs 轮询、证书互认
   - S7：DB 块地址、S7Comm 基本概念

### B 项目拷打（按 ADR 讲故事：问题→定位→修法）
- 断点续传（ADR-001）、重试超限丢弃 + 固定批量上限（2026-08-22 删 AIMD/死信）、磁盘保护（ADR-012）、双写、熔断、长连接池、重试退避、SignalR 实时推送、JWT+RBAC+WriteGuard、WPF 双端架构

### C 系统设计追问（无经验最怕，用本项目兜底）
- 断网续传怎么设计？多协议并发怎么串行化？海量点位采集性能？本地存储防爆？

### D 软技能/HR
- 2 年空白期：坦诚 + 学习证据（GitHub 提交历史、worklog、726 测试、部署演示）
- 「为什么没经验」→ 转为「我已完成什么、能做什么」

## 三、对应题库（docs/interview/<模块>/questions.md + answers.md）
protocol → collection → transport → forwarder → persistence → security → telemetry → device → domain → storage → desktop
- protocol 缺 **OPC UA 题库（待办**：按 docs/12-OPC-UA接入设计.md + OpcUaDriver.cs 补）
- desktop 已含 OPC UA / S7 桌面配置题（docs/12、docs/13 已实施落地）
- 刷法：先自答 questions.md → 对照 answers.md；能不看代码画出三条时序（批量读失败回退、RTU 串口串行、并发 GetOrCreate 竞争）即吃透

## 四、简历包装要点
- 一句话定位：.NET 物联网边缘网关工程师（工业协议采集 + 云边同步 + Vue/WPF 双端全栈）
- 项目主线一条（NitroGateway）+ 量化：多协议（Modbus TCP/RTU、S7、OPC UA）、MQTT QoS/遗嘱/断点续传、1s 采集引擎、726 单测、Docker Compose 部署
- 附 GitHub + 可运行演示（README 部署步骤）
