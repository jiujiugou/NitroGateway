# AC — OPC-UA 层次 2 订阅采集（P1-1）

> 状态：**实现 + 测试完成**。V-1 构建、V-2/V-4 单元测试全绿；V-3 订阅集成用例中
> 幂等/断开删除用例通过，依赖服务端推送通知的 3 个用例（初始值/变更/Bad 恢复）因进程内
> 参考服务器（`CustomNodeManager2`）不产生 DataChange 通知而 Timeout，属测试服务器模拟
> 限制，非生产驱动缺陷；留待测试服务器修复后回填 PASS（见 worklog 2026-09-02）。

## 范围

为 OPC-UA 增加 Subscription/MonitoredItem 推送采集；Good 通知经既有 Pipeline 与 Dispatcher
处理（缩放/死区/双写/转发），订阅不可用或驱动不支持时回退轮询；订阅生命周期与读写共用驱动闸门。

## ADR 引用

ADR-071（订阅/轮询共用采集管道 + `ISubscriptionSource`）、ADR-019（不产伪值、驱动 `_gate` 串行）、
ADR-053（死区抑制共用放行子集）、ADR-062（点位级 `ScanIntervalMs` 语义）。

## 不在本次范围

层次 1 Browse（ADR-070）、层次 3 KeepAlive/TransferSubscriptions、层次 4 安全与证书、
Modbus/S7 行为、`RawPointValue` 的 ServerTimestamp 扩展。

## AC

- AC-1：`ISubscriptionSource` 是 `Domain.Protocols` 的独立能力接口，`OpcUaDriver` 与
  `ReliableProtocolDriver` 均提供该能力；非订阅协议不受影响。
  - 行为：运行 `SubscriptionCoordinatorTests` 的接口/装饰器测试与 `ReliableProtocolDriverTests`
    （需新增用例），预期 PASS。
  - 源码检查（仅源码检查）：`Domain/Protocols/ISubscriptionSource.cs` 接口存在；
    `Protocol/OpcUa/OpcUaDriver.cs:29` 类声明实现 `ISubscriptionSource`；
    `Protocol/Abstraction/ReliableProtocolDriver.cs:30,101-142` 装饰器实现并透传。
- AC-2：OPC-UA 驱动能按设备创建一个 Subscription、按 enabled 点位创建 MonitoredItem；
  `ScanIntervalMs=0` 使用全局间隔，正值使用点位间隔。
  - 行为：运行 OPC-UA 订阅集成测试（需新增，并入 `OpcUaDriverIntegrationTests`），
    预期首次收到各点位初始值，服务端改值后收到变更通知，通知按发布间隔到达。
  - 源码检查（仅源码检查）：`OpcUaDriver.cs:189` PublishingInterval = 全局采集间隔；
    `:236` SamplingInterval = `ScanIntervalMs > 0 ? 点位间隔 : 全局间隔`。
- AC-3：Good 通知转换为 `RawPointValue` 并进入既有 Pipeline/Dispatcher；Bad/Uncertain 不产值。
  - 行为：运行协调器单测与 OPC-UA 订阅集成测试，预期 Dispatcher 收到 Good 快照、
    服务端置 Bad 时无快照产生。
  - 源码检查（仅源码检查）：`OpcUaDriver.cs:606-611` 非 Good 直接跳过；
    `Collection/Subscription/SubscriptionCoordinator.cs:99-101` 复用 `_pipeline.Process` →
    `_dispatcher.DispatchAsync`（唯一管道）。
- AC-4：订阅接管成功时，设备采集轮不再调用轮询 Reader；订阅建立失败或驱动不支持时仍执行原轮询。
  - 行为：运行 `SubscriptionCoordinatorTests`，预期 TryActivateAsync 返回 true 时
    DeviceCollector 直接 return（不调 Reader）、返回 false 时继续轮询，两种路径均 PASS。
  - 源码检查（仅源码检查）：`Collector/DeviceCollector.cs:79-80` 订阅激活即跳过轮询；
    `Subscription/SubscriptionCoordinator.cs:49-50,53-54,57-58,70-78,79-86` 能力检查与成功/失败分支。
- AC-5：OPC-UA Subscription 配置、Read、Write、Browse 与 Disconnect 继续经同一 `_gate` 串行；
  停止/释放驱动时订阅被删除。
  - 行为：运行 `OpcUaDriverSubscriptionTests`（需新增）与集成测试，预期
    Disconnect/Dispose 后 `IsSubscriptionActive == false`；重复 Ensure/Stop 幂等不抛异常。
  - 源码检查（仅源码检查）：`OpcUaDriver.cs:192,272` 订阅方法均在 `_gate` 内执行；
    `:168` Disconnect 先删订阅、`:583` Dispose 删除订阅（均经 `DeleteSubscriptionAsync`）。
- AC-6：范围外模块未改动。验证：检查变更仅位于 Domain Protocols、Protocol Abstraction/OpcUa、
  Collection（Subscription/DeviceCollector/DI）、测试、ADR、AC、worklog 与任务链文件。

## 验证命令

- V-1：`dotnet build NitroGateway.slnx`；预期 0 Error、0 阻塞。
- V-2：`dotnet test tests\NitroGateway.UnitTests --filter "FullyQualifiedName~SubscriptionCoordinatorTests|FullyQualifiedName~ReliableProtocolDriverTests"`；预期失败 0。
- V-3：`dotnet test tests\NitroGateway.IntegrationTests --filter "FullyQualifiedName~OpcUaDriverIntegrationTests"`；预期失败 0（含订阅集成用例）。
- V-4：`dotnet test tests\NitroGateway.UnitTests --no-restore`；预期失败 0（回归）。

## 实测回填（2026-09-02）

- V-1：PASS——`dotnet build NitroGateway.slnx`，0 Error（12 个既有 NU1701 警告，与本任务无关）。
- V-2：PASS——`SubscriptionCoordinatorTests`（12 用例）+ `ReliableProtocolDriverTests`（订阅 7 用例）全过。
- V-3：部分 PASS——旧用例（连接/读写/Ping/断连重连、服务端停机重启、Browse×3）通过；
  订阅幂等/断开删除用例通过；初始值/变更/Bad 恢复 3 用例 Timeout（见状态注记，测试服务器不推送 DataChange）。
- V-4：PASS——`dotnet test tests\NitroGateway.UnitTests --no-build`，809/809 通过。
