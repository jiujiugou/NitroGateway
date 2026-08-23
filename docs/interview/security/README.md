# Security 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Security`（认证 JWT + RBAC 授权 + 写保护 WriteGuard + 审计）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（概念/边界/数据流）· ★★ 进阶（实现细节/失败路径/安全攻防）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
SecurityServiceCollectionExtensions（总入口：配置绑定 + fail-fast + 全部注册）
→ JwtConfig + UserConfig（配置模型）
→ TokenGenerator（密码验证 + 签发）
→ LoginRateLimiter + AuthController（登录限流）
→ Roles + 各 Controller 的 [Authorize]（RBAC）
→ WriteGuard 系（写保护，预留未接线）
→ AuditMiddleware + ExceptionHandlingMiddleware（审计）
→ Program.cs 中间件顺序 → 攻防/开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| DI 入口 | `src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs` | 配置绑定 + 开发密钥降级 + fail-fast 校验 + 认证/授权/门控注册 |
| 配置模型 | `src/NitroGateway.Security/Auth/JwtConfig.cs`、`UserConfig.cs` | `Security` 节绑定；内置用户（只存 PasswordHasher 哈希） |
| 签发器 | `src/NitroGateway.Security/Auth/TokenGenerator.cs` | PasswordHasher 验密 + HMAC-SHA256 签发 JWT（Name/Role/jti） |
| 登录限流 | `src/NitroGateway.Security/Auth/LoginRateLimiter.cs` | 按 `用户名\|IP` 计失败：5 次/10 分钟窗口，锁 60 秒（内存实现） |
| 角色常量 | `src/NitroGateway.Security/Roles.cs` | Admin/Operator/Viewer 与 OR 组合常量 |
| 写保护 | `src/NitroGateway.Security/Guard/WriteGuard.cs` 等 4 文件 | Mode→Range→Rate 三级校验（ADR-004 P1-1，预留未接线） |
| 审计 | `src/NitroGateway.Security/Audit/AuditMiddleware.cs` | 记录 `/api` 的 Who/What/When/Result/IP，不记 body |
| 异常兜底 | `src/NitroGateway.Security/Audit/ExceptionHandlingMiddleware.cs` | 统一 500 JSON，注册在 Audit 内层（ADR-004 P2-4） |
| 登录端点 | `src/NitroGateway.Webapi/Controllers/AuthController.cs` | trim→空校验→限流→签发→计数/重置 |
| 接线点 | `src/NitroGateway.Webapi/Program.cs:41,112-113` | `AddNitroSecurity`、中间件顺序 |

## 跨模块依赖（答题时需要知道的上下文）

- **Webapi 控制器**：直接消费 RBAC——Alarms（查看全员/确认 Admin+Operator）、AlarmRules、PointImport（Admin+Operator）、Status/Devices/Measurements（全员）（DeadLetters 控制器 2026-08-22 删除）。
- **`IProtocolDriver.WriteAsync`（Protocol 模块）**：写保护的最终执行对象，当前无生产调用方（所以 Guard 未接线）。
- **Serilog**：审计与日志落点（`logs/nitrogateway-.log`，日滚动保留 7 天，CompactJson），见 `appsettings.json:16-29`。
- **ADR-004**（`notes/ADR/security/ADR-004-security-optimization.md`）：本模块加固依据——P1 写保护、P2 配置 fail-fast/限流/异常顺序、P3 登录与审计细节。
- **Dev 密钥**：`appsettings.json:53` 的 `NitroGateway-Dev...` 前缀会触发「随机密钥降级」，token 不跨重启——题 Q1.3/Q7.5 反复考这一点。

## 注意事项

- **代码是唯一事实来源**。题目里埋了「默认值陷阱」（`WriteCommand.DeviceStatus` 默认 Online）、「未接线能力」（WriteGuard）、「再锁行为」（锁到期后再错 1 次立即续锁）等坑题，答案以代码 + XML 注释为准。
- 测试是理解行为最快的捷径：`tests/NitroGateway.UnitTests`（`SecurityConfigValidationTests` / `TokenGeneratorTests` / `WriteGuardTests`，覆盖 fail-fast 三例、哈希验证明文失败、写保护 9 场景）。
- 答完所有题目后，试着不看代码画出「登录→鉴权→审计」和「写命令→三级校验」两条时序——能画出来就是吃透了。
