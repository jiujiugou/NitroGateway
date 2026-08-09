# Protocol 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Protocol`（协议驱动模块：Modbus TCP/RTU、S7、OPC UA 驱动 + 地址解析 + 复合工厂 + 长连接池 + 重试装饰器）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/并发/失败路径）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
IProtocolDriver（契约）→ 地址解析（PointAddress / IAddressParser）
→ 复合工厂（ProtocolDriverFactory）→ 驱动池（ProtocolDriverPool）
→ 重试装饰器（ReliableProtocolDriver）→ Modbus 批量读优化
→ Modbus TCP/RTU 差异 → 串口共享（SerialPortManager）
→ S7 → OPC UA（未入 slnx）→ 测试与开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 驱动契约 | `src/NitroGateway.Domain/Protocols/IProtocolDriver.cs` | 统一读写接口，全返回 OperationResult，定义在 Domain 实现依赖倒置 |
| 状态/能力 | `src/NitroGateway.Domain/Protocols/DriverState.cs`、`DriverCapability.cs` | 四态状态机；批量/订阅/批量上限能力声明 |
| 地址抽象 | `src/NitroGateway.Protocol/Abstraction/PointAddress.cs`、`IAddressParser.cs` | 协议无关地址基类；解析/序列化/距离计算 |
| 复合工厂 | `src/NitroGateway.Protocol/Abstraction/ProtocolDriverFactory.cs` | 各协议 Register 自己，Create 时统一包 ReliableProtocolDriver |
| 驱动池 | `src/NitroGateway.Protocol/Abstraction/ProtocolDriverPool.cs` | 按设备复用长连接，指纹变化重建，并发唯一存活 |
| 重试装饰器 | `src/NitroGateway.Protocol/Abstraction/ReliableProtocolDriver.cs` | 批量读 3 次重试 + 指数退避 + 3s 独立超时 + 自动建连 |
| DI 注册 | `src/NitroGateway.Protocol/NitroGateway.Protocols/ProtocolServiceCollectionExtensions.cs` | 注册 Modbus/S7 与单例工厂、驱动池 |
| Modbus 解析 | `src/NitroGateway.Protocol/Modbus/ModbusAddressParser.cs`、`ModbusAddress.cs`、`ModbusArea.cs` | PLC 式地址 → 0-based 偏移；四功能区；数据类型→寄存器数 |
| Modbus 基类 | `src/NitroGateway.Protocol/Modbus/ModbusDriverBase.cs` | 单点读写、批量合并读、Ping、错误转换，TCP/RTU 公共逻辑 |
| Modbus TCP | `src/NitroGateway.Protocol/Modbus/ModbusTcpDriver.cs` | HslCommunication ModbusTcpNet，异步 API，错误分类 |
| Modbus RTU | `src/NitroGateway.Protocol/Modbus/ModbusRtuDriver.cs` | 串口驱动，共享闸门 + 从站号切换 |
| 串口管理 | `src/NitroGateway.Protocol/Modbus/SerialPortManager.cs` | 同端口多从站共享句柄，引用计数释放，参数冲突拒绝 |
| 批量规划 | `src/NitroGateway.Protocol/Modbus/ModbusBatchPlanner.cs` | 同类型连续段切分，防止连读错位 |
| S7 | `src/NitroGateway.Protocol/S7/S7Driver.cs`、`S7AddressParser.cs` | Siemens S7Net，DB 地址解析（含已知简化点） |
| OPC UA | `src/NitroGateway.Protocol/OpcUa/OpcUaDriver.cs`、`OpcUaAddressParser.cs`、`IBrowseableDriver.cs` | OPC UA 驱动 v1（Connect 未实现）；强类型 NodeId；Browse 能力独立接口 |

## 跨模块依赖（答题时需要知道的上下文）

- `ProtocolIdentifier` / `DeviceConnection` / `DevicePoint` / `DataType`：Domain 模块，连接参数与点位定义（`src/NitroGateway.Domain/Devices/`）
- `OperationResult` / `OperationalError`：Shared 模块，统一成功/失败语义（Timeout / Communication / Protocol / Unavailable 分类）
- `IProtocolDriverPool`：被 Collection 的 `DeviceReader` 使用（长连接复用），被 Device 模块在设备变更/删除时 `Evict`
- `ReliableProtocolDriver`：最终失败只打 Debug，Warning 由上层 `DeviceCollector` 补（持有设备名上下文）
- HslCommunication：Modbus/S7 的底层通信库（第三方，协议细节以其为准）

## 注意事项

- **代码是唯一事实来源**。批量读相关演进（ADR-003 P1/P2/P3）都写成了代码注释，答题以代码 + XML 注释 + 测试为准；文档与代码不一致处题目里会埋（如 ReliableProtocolDriver 注释「只打 Debug」与实际 `OnRetry` 打 Information 不一致）。
- **OPC UA / Mitsubishi 未入 slnx**：源码存在但不参与构建，答题时注意区分「已接入」与「未启用」。
- 测试是理解行为最快的捷径：`tests/NitroGateway.UnitTests`（ProtocolDriverPoolTests / ModbusAddressParserTests / ModbusBatchPlannerTests）、`tests/NitroGateway.IntegrationTests`（ModbusTcpDriverIntegrationTests 真实 TCP 回环）。
- 答完所有题目后，试着不看代码画出三条时序：① 批量读合并→失败回退→Faulted→重连；② 同端口两个 RTU 从站的帧级串行；③ 两个线程同时 GetOrCreate 同一设备不同配置的竞争结果——能画出来就是吃透了。
