# ADR-008: Domain 文档漂移问题清单

- 日期: 2026-08-07
- 用途: 出 Domain 面试题时扫描发现；供后续 agent 直接使用，修复后在代码加注释并删除本清单对应条目

## 条目

- P1-1 Deadband 语义文档漂移：`src/NitroGateway.Domain/Devices/DevicePoint.cs` 的 `Deadband` XML 注释写「相邻两次采集值的变化小于此值时不触发上报」，但 `src/NitroGateway.Collection/Pipeline/PointValuePipeline.cs` 实际实现为「死区只影响上次工程值缓存更新（供告警 Duration 判定），不丢弃数据，快照照常下发/存储/推送」。修复方向：以 Pipeline 实现为准，改 DevicePoint 注释为「变化小于此值时仅抑制告警 Duration 判定，不丢弃数据」。
- P2-1 Domain README 依赖描述漂移：`src/NitroGateway.Domain/README.md` 声称「不依赖任何其他项目」，但 `NitroGateway.Domain.csproj` 实际引用 `NitroGateway.Shared`（IProtocolDriver 依赖 OperationResult）。修复方向：README 改为「除无依赖的 Shared（OperationResult/OperationalError）外不依赖其他项目」；worklog 2026-08-07 已标记待确认，此处正式登记。
