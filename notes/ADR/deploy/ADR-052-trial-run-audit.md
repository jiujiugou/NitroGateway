# ADR-052: 试运行前审计——生产安全与部署决策

- 日期: 2026-08-16 | 状态: 问题 1/2 已实施；问题 3/4 观察（试运行期间持续关注）
- 来源: 试运行前审计（保留/磁盘/优雅关闭/日志/生产变量逐项核查）
- 关联: ADR-002（保留/磁盘）、ADR-012（DiskGuard）、ADR-025（Ingest）、ADR-035（边缘/中心拆分）、ADR-022（JWT 强制）

## Context

试运行前审计确认：保留策略（30 天/24h，注册于全部进程）、磁盘保护（DiskGuard 三联动）、优雅关闭、日志轮转（Day + 保留 7 份）、JWT 强校验（ChangeMe/短密钥拒启，Dev 前缀自动随机）均已就绪。遗留四个问题：问题 1（单现场 compose 网关 MQTT 指向自身断链）；问题 2（生产未覆盖默认测试账号）；问题 3（中心库双进程迁移+保留）；问题 4（1s 全量快照 30 天体积偏大）。

## Decision

- D1 问题 1（高，配置）：docker-compose.yml gateway 新增 MQTT__Host=mqtt + MQTT__Port=1883，容器内转发连 broker 服务名，数据链路恢复上行。
- D2 问题 2（中，安全）：两个 compose 均新增必填 ADMIN_PASSWORD/OPERATOR_PASSWORD/VIEWER_PASSWORD（Security__Users__0/1/2__Password）强制覆盖内置密码，未设置直接拒绝启动；SecurityServiceCollectionExtensions 非 Development 启动校验逐个用户用 PasswordHasher 比对 DefaultTestPasswords（admin123/oper123/view123），命中即抛 InvalidOperationException 拒启；FormatException（非标准哈希）跳过。
- D3 问题 2（补充，明文归一化）：AddNitroSecurity 配置加载阶段新增明文归一化（IsHashedPassword 按版本字节判定，非哈希一律按明文）；生产先拒绝默认测试密码再 HashPassword 写回，TokenGenerator 不变（解决 compose/.env 传明文密码登录 500：FormatException: not a valid Base-64 string）。
- D4 问题 3/4（中，不修，观察）：中心库双进程迁移+保留（busy_timeout=5s 兜底、低危，试运行观察）；1s 全量快照 30 天体积偏大（10 设备约 600 点 × 1/s ≈ 5200 万行/天 ≈ 390GB/30 天，试运行后按需下调保留天数或加变化检测/降采样/长周期聚合——功能变更需用户拍板）。

## Alternatives

- 仅强制覆盖 admin 密码：operator/viewer 仍默认测试密码 → 生产拒启崩溃循环（已补全三账号）。
- compose 传明文密码直接入库：代码只认 PasswordHasher 哈希 → 登录 500（必须归一化）。

## Rationale

生产环境不得存在默认测试密码、缺变量应直接拒启暴露配置错误；明文密码必须归一化为哈希才能登录；问题 3/4 属低危/量级问题，试运行期观察、试运行后按数据实测决策。

## Consequences

- 生产缺变量直接拒启；默认测试密码拒启；明文密码自动归一化可登录。
- 问题 3（中心库双进程迁移+保留）与问题 4（1s 全量快照体积测算）列入观察项，详见 worklog。
