# ADR-013: Verification 构建修复（已确认完成，收尾确认）

- 日期: 2026-08-07 | 状态: P1 已完成（实测 0 错误） | 用途: 闭环 Q-01 遗留的 Verification 构建失败（原 D-07）
- 范围: tests/NitroGateway.Verification

## 现状
- 2026-08-07 实测 `dotnet build tests/NitroGateway.Verification`：**0 错误**；csproj 现仅引用 Domain/Shared/Device/Collection/Persistence，Q-01 记载的幽灵引用（Infrastructure.Sqlite/Scheduler）已移除
- 残留 6 警告: NU1903（SQLitePCLRaw.lib.e_sqlite3 2.1.11 高危漏洞 ×3，间接依赖）、NU1504（Telemetry csproj 重复 OpenTelemetry PackageReference）

## 处置
- P1（已完成）: 幽灵引用移除，构建通过，无需再改
- P2（待用户拍板）: Verification 未入 slnx、CI 不覆盖——保持「本地闭环验证工具」定位（推荐），或加回 slnx
- P3（可做）: 清理 Telemetry 重复 PackageReference（非依赖升级，不违反雷区）；NU1903 涉及依赖升级，按 AGENTS.md 不升级，记录待用户决定

## 验证
- 已实测 build 0 错误（2026-08-07）
