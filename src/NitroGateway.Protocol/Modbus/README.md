# Modbus

Modbus RTU（串口）/ Modbus TCP（网口）协议驱动实现，基于 HslCommunication。

## TCP 连接要点

- 端口缺省为 502（Modbus 标准，HslCommunication 默认是 5000，驱动已显式覆盖）。
- 字节序默认 ABCD（高字在前）；设备字序特殊时通过 `Parameters["DataFormat"]` 调整。
- 通信全部走 HslCommunication 异步 API；批量读全部点位失败时驱动复位为 Faulted，
  上层重试管线会重新建连（不会一直打在死连接上）。
- 驱动经 `IProtocolDriverPool` 按设备复用（长连接）：连接参数不变时保持 socket/串口打开，
  设备更新/删除/下线时由 `DeviceManager` 驱逐释放；断线后由 Faulted 状态触发下一轮自动重连。
  测试连接接口除外，始终新建独立连接验证。
## 驱动分发

`Register("Modbus", ...)` 按 `connection.Parameters["Transport"]` 分发：

| Transport | 驱动 | 说明 |
| --- | --- | --- |
| `TCP`（缺省） | `ModbusTcpDriver` | 网口通信，Endpoint 形如 `192.168.1.100:502` |
| `RTU` | `ModbusRtuDriver` | 串口通信，Endpoint 为串口名（`COM3` / `/dev/ttyUSB0`） |

## RTU 串口参数

RTU 驱动从 `connection.Parameters` 读取：

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `UnitId` | 1 | 从站地址（1-247） |
| `BaudRate` | 9600 | 波特率 |
| `DataBits` | 8 | 数据位（7/8） |
| `Parity` | None | 校验位：None/Even/Odd/Mark/Space |
| `StopBits` | One | 停止位：One/Two |
| `DataFormat` | ABCD | 寄存器字节序：ABCD（标准）/CDAB/BADC/DCBA |

## 串口资源管理

- `ISerialPortManager`（Singleton）统一管理物理串口：同一端口只打开一个 `ModbusRtu` 句柄，多个从站驱动通过 `SerialPortLease` 共享。
- 每次通信（单点/批量读、写、Ping）都在共享 `SemaphoreSlim` 闸门内进行，先切换 `Station` 再发帧，保证帧级串行与多从站复用。
- 同一端口以不同串口参数被占用时会抛出异常；最后一个租约释放后关闭串口。
- 可用串口与占用状态：

```http
GET /api/devices/serial-ports          # 系统可用串口列表
GET /api/devices/serial-port-status    # 当前打开串口的状态快照
```

## Docker（Linux 主机）

容器内访问物理串口需要设备透传，在 `docker-compose.yml` 的 gateway 服务下挂载：

```yaml
devices:
  - "/dev/ttyUSB0:/dev/ttyUSB0"
  - "/dev/ttyS0:/dev/ttyS0"
```
