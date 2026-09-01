# ADR-066: 用户管理不走全量 Identity——DB 化 users 表 + 管理接口（B1/A5 定案）

- 日期: 2026-08-23 | 状态: 已实施 | 关联: ADR-004（Security 优化）、ADR-022（Webapi review）、ADR-065（B1/A5）
- 一句话结论: 需要「用户管理」≠ 要上 ASP.NET Core Identity；在现有 JWT + RBAC + PasswordHasher 之上，把用户从配置文件迁到 SQLite users 表 + Admin 管理接口即可，全量 Identity 与分层/迁移纪律冲突且带来无用复杂度。

## 决策
- 不引入全量 ASP.NET Core Identity（Cookie 中间件 / EF Identity Store / 外部登录 / 2FA / 邮箱验证均无需求）。
- 用户数据落地 SQLite：FluentMigrator 新增 users 表（M015），配置文件只作首启种子，不再承载运行时账号。
- 方案细节:
  1. M015 迁移: users 表（Id / Username 唯一 / PasswordHash / Role / IsEnabled / CreatedAt / UpdatedAt / LastLoginAt）。
  2. 种子: 首启空表灌入当前配置用户（保住 admin/admin123 开发登录）；之后配置项仅引导。
  3. UserController（`[Authorize(Policy="AdminOnly")]`）: 列表 / 新增 / 改角色 / 启停 / 重置密码 / 自助改密。
  4. `TokenGenerator` 改读用户存储；JWT 签发、RBAC 策略、限流不变。
  5. 代码位置: `src/NitroGateway.Security/Auth` + `src/NitroGateway.Persistence`（Migrations + Sqlite）+ `src/NitroGateway.Webapi/Controllers`。

## 为什么
- 用户目前是配置项：`appsettings.json` `Security:Users`，密码用 `PasswordHasher<UserConfig>` 哈希（本就是 Identity 的 hasher）。
- JWT 签发/校验（`TokenGenerator` + JwtBearer）、RBAC 三角色五策略、登录限流（`LoginRateLimiter`）、审计落库（M014 + `AuditMiddleware`）均已就绪且已验证（ADR-004/022）。
- 固定 3 角色、个位数用户、无动态角色/细粒度权限/2FA 需求 → Identity 冗余。
- 架构约束: `Domain/` 不引用基础设施；库结构变更走 FluentMigrator。Identity EF Store 会把 `IdentityUser` 带进领域层，并与 FluentMigrator 双轨冲突。

## 影响与后果
- 用户管理入口从配置文件迁到 DB：新增/改密/启停即时生效，无需改配置重启；登录与授权行为与现状一致。
- 配置项仅作首启种子，不再承载运行时账号。

## 什么时候才该上 Identity（防御性边界）
- 出现动态角色/权限点授权、跨重启持久化锁定、2FA/邮箱/短信验证等 SaaS 级能力时再评估，届时本 ADR 再议。
