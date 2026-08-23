# ADR-066: 用户管理不走全量 Identity——DB 化 users 表 + 管理接口（B1/A5 定案）

- 日期: 2026-08-23 | 状态: 已实施 | 关联: ADR-004（Security 优化）、ADR-022（Webapi review）、ADR-065（B1/A5）、worklog 2026-08-23
- 一句话结论: 需要「用户管理」≠ 要上 ASP.NET Core Identity；在现有 JWT + RBAC + PasswordHasher 之上，把用户从配置文件迁到 SQLite users 表 + Admin 管理接口即可，全量 Identity 与分层/迁移纪律冲突且带来无用复杂度。

## 决策
- 不引入全量 ASP.NET Core Identity（Cookie 中间件 / EF Identity Store / 外部登录 / 2FA / 邮箱验证均无需求）。
- 用户数据落地 SQLite：FluentMigrator 新增 users 表（M015），配置文件只作首启种子，不再承载运行时账号。

## 为什么（现状核对，2026-08-23）
- 用户目前是配置项：`appsettings.json` `Security:Users`，密码用 `PasswordHasher<UserConfig>` 哈希（本就是 Identity 的 hasher）。
- JWT 签发/校验（`TokenGenerator` + JwtBearer）、RBAC 三角色五策略、登录限流（`LoginRateLimiter`）、审计落库（M014 + `AuditMiddleware`）均已就绪且已验证（ADR-004/022）。
- 固定 3 角色、个位数用户、无动态角色/细粒度权限/2FA 需求 → Identity 冗余。
- 架构约束: `Domain/` 不引用基础设施；库结构变更走 FluentMigrator。Identity EF Store 会把 `IdentityUser` 带进领域层，并与 FluentMigrator 双轨冲突。

## 实施（后续代码改动，按序）
1. M015 迁移: users 表（Id / Username 唯一 / PasswordHash / Role / IsEnabled / CreatedAt / UpdatedAt / LastLoginAt）。
2. 种子: 首启空表灌入当前配置用户（保住 admin/admin123 开发登录）；之后配置项仅引导。
3. UserController（`[Authorize(Policy="AdminOnly")]`）: 列表 / 新增 / 改角色 / 启停 / 重置密码 / 自助改密。
4. `TokenGenerator` 改读用户存储；JWT 签发、RBAC 策略、限流不变。
5. 代码位置: `src/NitroGateway.Security/Auth` + `src/NitroGateway.Persistence`（Migrations + Sqlite）+ `src/NitroGateway.Webapi/Controllers`。

## 验证标准
- 新增/改密/启停即时生效（无需改配置重启）；登录与授权行为与现状一致；附单元测试（含 RBAC 断言）；收尾 build + 全量 test。

## 实施完成（2026-08-23）
- M015 迁移 users 表（id 自增 / username 唯一 / password_hash / role / is_enabled 默认 true / 时间列 O 格式 UTC 字符串，沿用 M014 约定）。
- `SqliteUserStore`（Dapper 独立连接，与 MeasurementStore 同模式）：Find/Create/UpdateRole/UpdateEnabled/UpdatePasswordHash/UpdateLastLogin/Delete/List + `SeedIfEmptyAsync`（空表单事务批量灌入配置用户，保住 admin/admin123 开发登录）。失败语义：身份源是权威，存储异常上抛转 500（不伪装 401）；仅用户名唯一冲突（SQLITE_CONSTRAINT=19）映射 Validation 失败。
- `IUserStore` 接口定义在 Security 模块（纯契约），SQLite 实现位于 Persistence（依赖方向 Security ← Persistence）。
- `TokenGenerator` 同步 IssueToken → 异步 `IssueTokenAsync`，依赖改为 `IUserStore` + `PasswordHasher<UserAccount>`（哈希格式与 UserConfig 兼容，种子可直读）；登录每次实时读库（新增/改密/启停即时生效）；停用拒签（Disabled）；登录成功 best-effort 刷新 LastLoginAt；`PasswordHasher<UserAccount>` 注册单例。
- `UserController`（`api/user`）：Admin 管理动作逐方法 `[Authorize(Policy="AdminOnly")]`（列表/新增/改角色/启停/重置密码/删除）；`PUT me/password` 自助改密对任意已登录角色开放（类级+方法级 [Authorize] 是叠加关系，故不写类级）；密码最小 8 位；禁止移除最后一个启用 Admin（CanRemoveAdminAsync）；DTO 不含密码哈希。
- `AuthController.Login` 异步化：UserNotFound/InvalidPassword 统一 401（不泄露账号存在性），Disabled 单独 403；登录限流不变。
- `Program.cs`：`InitializeDatabase()` 后执行首启种子 `SeedIfEmptyAsync`。
- 验证：`dotnet test tests/NitroGateway.UnitTests` **754 通过 0 失败**（基线 738 + 16 新增：TokenGeneratorTests 6 例、SqliteUserStoreTests 10 例、WebapiAuthorizationTests 补 3 例）；`dotnet build NitroGateway.slnx` **0 错误**；`dotnet test tests/NitroGateway.IntegrationTests` **51 通过 0 失败**。git 未提交（默认由用户执行）。

## 什么时候才该上 Identity（防御性边界）
- 出现动态角色/权限点授权、跨重启持久化锁定、2FA/邮箱/短信验证等 SaaS 级能力时再评估，届时本 ADR 再议。
