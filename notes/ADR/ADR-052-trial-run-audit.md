# ADR-052: 试运行前审计发现（2026-08-16）

- 日期: 2026-08-16 | 状态: 问题 1/2 已修复；问题 3/4 观察（试运行期间持续关注） | 来源: 试运行前审计（保留/磁盘/优雅关闭/日志/生产变量逐项核查）
- 关联: ADR-002（保留/磁盘）、ADR-012（DiskGuard）、ADR-025（Ingest）、ADR-035（边缘/中心拆分）、ADR-022（JWT 强制）

## 结论概览
- 已就绪: 保留策略（30 天/24h，注册于全部进程）、磁盘保护（DiskGuard 三联动）、优雅关闭（桌面 Closing→StopAsync 排空；Web/Ingest 由 ASP.NET 托管 SIGTERM）、日志轮转（Day + 保留 7 份）、JWT 强校验（ChangeMe/短密钥拒启，Dev 前缀自动随机）。
- 已修复: 问题 1（单现场 compose 网关 MQTT 指向自身断链）与问题 2（生产未覆盖默认测试账号），详见下方「已修复」。
- 观察（试运行期间）: 问题 3（中心库双进程迁移+保留）、问题 4（1s 全量快照 30 天体积偏大）。

## 已修复（2026-08-16）
- 问题 1（高，配置）: `docker-compose.yml` gateway 新增 `MQTT__Host=mqtt` + `MQTT__Port=1883`，容器内转发连 broker 服务名，数据链路恢复上行。
- 问题 2（中，安全）: 两个 compose 均新增 `Security__Users__0__Password=${ADMIN_PASSWORD:?}` 强制覆盖内置 admin 密码（未设置直接拒绝启动）；新增 `.env.example` 模板（JWT_SECRET + ADMIN_PASSWORD，`.env` 已 gitignore）。
  - 代码侧: `src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs` 非 Development 环境启动校验，逐个用户用 `PasswordHasher` 比对 `DefaultTestPasswords = ["admin123", "oper123", "view123"]`，命中即抛 `InvalidOperationException` 拒启；`FormatException`（非标准哈希）跳过。
  - 已验证: 三个内置哈希实测分别匹配 admin123/oper123/view123；新增 3 单测，全量 612/612 通过；`docker compose config` 两种形态均解析通过（缺变量时报错符合预期）。

## 问题 3（中，观察）：中心库 center.db 被 ingest 与 gateway(Center) 双进程迁移+保留
- 位置: `src/NitroGateway.Ingest/Program.cs` 与 `src/NitroGateway.Webapi/Program.cs`（Center 模式）均调 AddNitroSqlite + InitializeDatabase
- 问题: 两容器启动并发 FluentMigrator 建表、24h 保留清理同一 center.db；busy_timeout=5s 兜底，FACTORY-TEST T7 已验证可跑，属低危；重复清理浪费。
- 修复方向（非本轮必做）: Center 模式下 gateway 跳过 AddNitroSqlite 的 hosted services（迁移/保留/磁盘守卫由 ingest 独占），或 gateway 用只读连接串。

## 问题 4（中，量级）：数据量测算——1s 全量快照 30 天体积偏大
- 位置: `src/NitroGateway.Collection/Collector/DeviceCollector.cs`（每轮全量快照，无变化检测）→ SqliteMeasurementStore.WriteAsync
- 测算: 10 设备约 600 点 × 1/s ≈ 5200 万行/天 ≈ 30 天 15 亿行；按 ~250B/行 ≈ 13GB/天、390GB/30 天。SQLite 单表十亿行 + 每日全量 purge，查询/保留显著变慢（用户此前判断"数据量不算大"需修正）。
- 修复方向（试运行后，功能变更需用户拍板）: 先实测 `COUNT(*) WHERE timestamp > 24h前` 校准行/天；按需下调保留天数（如 7 天）、或加变化检测/降采样/长周期聚合。
