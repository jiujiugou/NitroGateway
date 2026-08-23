# ADR-004: Security 模块优化清单（边缘网关适配范围）

- 日期: 2026-08-07
- 状态: 全部条目已处理（2026-08-07）
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除对应条目
- 范围: src/NitroGateway.Security 全模块 + Webapi 接线（AuthController / Program.cs / 各 Controller 授权标注）；只做边缘网关适配所需，不做用户管理系统/OAuth/SSO/密码过期等超范围设计

## 处理记录（2026-08-07）

- P1-1 写指令门控未接线：确认网关当前无需写操作 API，采用「预留能力」路线——SecurityServiceCollectionExtensions 注册处注释标记「写门控为预留能力，未接线」，docs/03-功能清单.md F-28 同步标注；后续如需下发控制指令，另起任务新增写端点并接入 WriteGuard
- P1-2 密码明文回退：TokenGenerator.IssueToken 移除明文 Equals 兜底（仅保留 VerifyHashedPassword）；UserConfig.Password 注释改为「哈希存储」；appsettings.json 三个开发账号密码改为 PasswordHasher 哈希（明文口令不变：admin/admin123、operator/oper123、viewer/view123）
- P2-1 登录限流：新增 LoginRateLimiter（内存实现，按「用户名|IP」计数，默认 5 次/10 分钟窗口触发 60 秒锁定）；AuthController 接线（锁定返回 429 + 剩余秒数，成功 Reset）；DI 注册
- P2-2 JWT 配置校验：AddNitroSecurity fail-fast——JwtSecretKey 字节数 ≥32、ExpireHours ≥1，违规抛 InvalidOperationException
- P2-3 角色校验：AddNitroSecurity 启动时校验 UserConfig.Role ∈ {Admin, Operator, Viewer}
- P2-4 审计异常兜底：新增 ExceptionHandlingMiddleware（未处理异常统一转 500 JSON），Program.cs 注册在 AuditMiddleware 内层（后于其注册），异常先转 500 再被外层审计记录，避免审计丢失
- P3-1 角色常量：Roles.cs 新增 AdminOperator/AllRoles 常量，7 个控制器共 8 处 [Authorize]（AlarmsController 类+方法各一处）全部改用 Roles 常量，避免角色改名漏改
- P3-2 登录输入 Trim：AuthController.Login 用户名 Trim 后再校验
- P3-3 审计不记请求体：按适配范围刻意不记 body（敏感数据），AuditMiddleware 注释说明决策
- 验证: build 0 错；UnitTests 193 通过（上轮 174 + 19）；IntegrationTests 14 通过（上轮 12 + 2）
