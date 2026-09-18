# NitroGateway.ApiE2eTests（L3 API 端到端）

**本工程是什么**：用 `WebApplicationFactory<Program>` 在测试进程内**真起 NitroGateway.Webapi 组合根**
（DI、中间件、SQLite 迁移、用户种子、HostedService），通过 HTTP 走完整链路断言。
不是控制器直调（那是 UnitTests 干的事），不是需要外部 MQTT/Modbus 模拟器的场景（那归 FACTORY-TEST）。

## 覆盖范围（docs/08-测试策略.md §2 L3）

- 认证：admin 登录拿 JWT、错误密码 401、无 Token 401（FACTORY T0.6 / T5.3 的自动化版）。
- RBAC：viewer 越权删设备 403、operator 可读（FACTORY T5.1/T5.2 的自动化版）。
- 设备/点位 CRUD：建/列/查/导出/删 + 非法 DataType 400，回读 SQLite 落库证据。
- 系统端点：`/healthz` 200、Swagger（开发态）可达、`/api/status/system` 鉴权可用。

## 运行

```bash
dotnet test tests/NitroGateway.ApiE2eTests
```

> 前置：与其它测试相同，**先停掉本机常驻的 NitroGateway.Webapi**（否则其 DLL 被锁，build 报 MSB3027）。
> 无需 Docker、MQTT broker、Modbus 模拟器——数据库用临时文件，Dispose 自动清理。

## 边界（诚实声明）

- 数据面"采集→入库→转发"（需要真实协议模拟器）不在本层，属 L2 进程内集成 / FACTORY-TEST T1~T3。
- MQTT Host/Port 被强制指向本机无监听端口，避免测试误连 appsettings 默认公网 broker。

## 新测试规矩

- 放对层：REST 契约/鉴权/中间件链路 → 本工程（方法名 `E_` 前缀）。
- 单模块逻辑 → `NitroGateway.UnitTests`；进程内组件协作 → `NitroGateway.IntegrationTests`。
- 不依赖测试间共享数据：每个用例自建设备自清理，命名带 GUID。
