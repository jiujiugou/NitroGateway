# Security 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 9 组 50 题；参考答案见 `answers.md`。

---

## 一、模块总览与 DI 注册

**Q1.1 ★** `AddNitroSecurity` 从配置到注册共做了哪几件事？按什么顺序？（提示：配置绑定→开发密钥降级→fail-fast→注册→认证→授权→门控→限流）
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:15`。

**Q1.2 ★** 为什么 JwtConfig、TokenGenerator、WriteGuard、LoginRateLimiter 全部是 Singleton？它们各自「无状态/线程安全」的前提是什么？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:47-51,92-98`。

**Q1.3 ★★** 开发密钥降级分支的触发条件是什么？生成的密钥长什么样？对已签发 token 有什么影响？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:20-31`。

**Q1.4 ★★** 三处 fail-fast 校验（密钥长度、过期小时、角色白名单）为什么必须在启动时抛异常，而不是运行时报 401/500？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:34-45`。

---

## 二、JWT 与 Token 签发

**Q2.1 ★** JwtConfig 绑定配置的哪一节？Issuer / Audience / ExpireHours 的默认值是什么？项目实际值是多少？
代码定位：`src/NitroGateway.Security/Auth/JwtConfig.cs:6-18`；`src/NitroGateway.Webapi/appsettings.json:52-56`。

**Q2.2 ★★** TokenGenerator 用什么验证密码？为什么配置文件里存明文密码一定登录失败？（ADR-004 P1-2）
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:42-47`；`src/NitroGateway.Security/Auth/UserConfig.cs:9`。

**Q2.3 ★** 签发的 token 里有哪些 Claim？jti 的用途是什么？当前代码有没有消费它？
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:52-57`。

**Q2.4 ★** 签名用什么算法、什么密钥类型？签发端和验证端的密钥分别从哪来、如何保持一致？
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:49-50`；`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:54-68`。

**Q2.5 ★★** TokenValidationParameters 显式开启了哪四项校验？没有显式设置的 ClockSkew 默认是多少？它带来什么影响？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:59-68`。

**Q2.6 ★★** 签发成功/失败分别记什么日志？从「最小泄露」角度看，这些日志里有什么信息值得注意？
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:37,45,66`。

---

## 三、登录与限流

**Q3.1 ★** 登录接口的完整处理顺序是什么？每一步对应什么状态码？哪些失败会触发计数？
代码定位：`src/NitroGateway.Webapi/Controllers/AuthController.cs:26-53`。

**Q3.2 ★★** 限流 key 为什么是 `username|IP`？只按用户名会有什么问题？只按 IP 呢？
代码定位：`src/NitroGateway.Webapi/Controllers/AuthController.cs:56-57`；`src/NitroGateway.Security/Auth/LoginRateLimiter.cs:6-7`。

**Q3.3 ★** 限流默认参数：多少次失败、什么窗口、锁多久？窗口过期后计数如何重置？
代码定位：`src/NitroGateway.Security/Auth/LoginRateLimiter.cs:17-24,52-54`。

**Q3.4 ★★** RecordFailure 在「已锁定」状态下为什么直接返回原 Entry，而不是继续累加/重置计时？锁到期后再失败一次会发生什么？
代码定位：`src/NitroGateway.Security/Auth/LoginRateLimiter.cs:49-50,56-58`。

**Q3.5 ★★★** 这个限流器是内存实现，有三个边界（多实例、重启、字典只增不减）——分别导致什么后果？类注释里如何声明它的定位？
代码定位：`src/NitroGateway.Security/Auth/LoginRateLimiter.cs:5-7,12,63`。

---

## 四、RBAC 与授权

**Q4.1 ★** Roles 里 5 个常量分别是什么？`AdminOperator = "Admin,Operator"` 为什么能直接用于 `[Authorize(Roles = ...)]`？
代码定位：`src/NitroGateway.Security/Roles.cs:15-19`。

**Q4.2 ★★** 说出本项目每个受保护控制器的角色要求（查看 vs 操作），并归纳「读」和「写/变更」的角色分层规律。（注：DeadLettersController 已于 2026-08-22 删除）
代码定位：`src/NitroGateway.Webapi/Controllers/AlarmsController.cs:13,42`、`AlarmRulesController.cs:14`、`PointImportController.cs:13`、`StatusController.cs:15`、`DevicesController.cs:12`、`MeasurementsController.cs:11`。

**Q4.3 ★★** 为什么告警「确认」只允许 Admin/Operator，而「查看」允许 Viewer？这体现了什么安全原则？
代码定位：`src/NitroGateway.Webapi/Controllers/AlarmsController.cs:13,42`。

**Q4.4 ★★** SignalR 为什么从 query string 读 access_token 而不是 Authorization 头？这个做法有什么风险？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:70-80`。

---

## 五、写保护 WriteGuard

**Q5.1 ★** WriteGuard 三个校验器的执行顺序？短路逻辑？为什么 Mode 排第一？
代码定位：`src/NitroGateway.Security/Guard/WriteGuard.cs:29-57`。

**Q5.2 ★** ModeValidator 的判定条件是什么？`"online"`、`"Online "`（带空格）能通过吗？
代码定位：`src/NitroGateway.Security/Guard/ModeValidator.cs:10-12`。

**Q5.3 ★** RangeValidator 在什么情况下跳过校验？上下边界值是允许还是拒绝？
代码定位：`src/NitroGateway.Security/Guard/RangeValidator.cs:10-19`。

**Q5.4 ★★** RateLimitValidator 的变化率公式是什么？两个跳过条件（PreviousValue 为 null、|prev| < 0.001）各自防什么？
代码定位：`src/NitroGateway.Security/Guard/RateLimitValidator.cs:10-18`。

**Q5.5 ★★★** 变化率校验的缺陷：① -50→0 算 100% 变化被放行，合理吗？② prev 为负时比值语义是什么？③ |prev| < 0.001 的跳过意味着 0.001→10（约 +999900%）也放行。结合「工业写保护」谈谈你会怎么改进。
代码定位：`src/NitroGateway.Security/Guard/RateLimitValidator.cs:15-18`。

**Q5.6 ★★★** WriteCommand.DeviceStatus 默认值是 "Online"——如果未来接线时调用方忘记填充真实状态，会发生什么？这是不是个隐患？
代码定位：`src/NitroGateway.Security/Guard/WriteCommand.cs:19`。

**Q5.7 ★★★** 现状：WriteGuard 已注册但没有任何生产调用点。要真正启用需要哪三步？「没有接线」为什么目前仍是安全的？一旦接线，安全边界在哪里？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:91-95`（注释提到 docs F-28）。

---

## 六、审计

**Q6.1 ★** AuditMiddleware 为什么在 `await _next` 之后才记录？在之前记录会缺什么信息？
代码定位：`src/NitroGateway.Security/Audit/AuditMiddleware.cs:20-24`。

**Q6.2 ★** 只审计 `/api` 前缀的原因？每条审计记录包含哪些字段？
代码定位：`src/NitroGateway.Security/Audit/AuditMiddleware.cs:26-36`。

**Q6.3 ★** 状态码 ≥400 用 Warning、否则 Information，这个分级的目的是什么？
代码定位：`src/NitroGateway.Security/Audit/AuditMiddleware.cs:40-51`。

**Q6.4 ★★** 为什么刻意不记录请求体？如果要记录 body 摘要需要做什么准备？（ADR-004 P3-3）
代码定位：`src/NitroGateway.Security/Audit/AuditMiddleware.cs:38-39`。

**Q6.5 ★★★** Program.cs 里 AuditMiddleware 在外、ExceptionHandlingMiddleware 在内。如果顺序颠倒，异常请求的审计会怎样？为什么？
代码定位：`src/NitroGateway.Webapi/Program.cs:112-113`；`src/NitroGateway.Security/Audit/ExceptionHandlingMiddleware.cs:6-7`。

**Q6.6 ★★** ExceptionHandlingMiddleware 里 `context.Response.HasStarted` 为 true 时为什么直接 rethrow？
代码定位：`src/NitroGateway.Security/Audit/ExceptionHandlingMiddleware.cs:29-30`。

---

## 七、攻防场景（面试官出招）

**Q7.1 ★★** 攻击者把 JWT payload 里的 role 改成 "Admin" 再发请求，结果是什么？哪一层拦住的？
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:49-50`（签名）；`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:59-68`（验证）。

**Q7.2 ★★** Viewer 用户调用「告警确认」接口，返回 401 还是 403？为什么？
代码定位：`src/NitroGateway.Webapi/Controllers/AlarmsController.cs:42`。

**Q7.3 ★★★** 攻击者要绕过登录限流，有哪些路径（换 IP、换用户名、多实例）？如果网关部署在反向代理后面，`RemoteIpAddress` 会是什么？会造成什么新问题？
代码定位：`src/NitroGateway.Webapi/Controllers/AuthController.cs:56-57`。

**Q7.4 ★★★** 攻击者故意输错 5 次密码把 admin 锁 60 秒——这是 DoS 漏洞吗？你会怎么缓解？
代码定位：`src/NitroGateway.Security/Auth/LoginRateLimiter.cs:17-24`。

**Q7.5 ★★★** 生产环境误配置了 `NitroGateway-Dev...` 前缀的密钥，会发生什么？（提示：降级分支不是报错而是换随机密钥）这个降级和 fail-fast 的冲突是什么？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:20-31,34-35`。

**Q7.6 ★★★** 已签发的 token 在 8 小时内无法吊销。员工离职场景怎么处理？给出至少三种缓解方案及各自代价。
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:59-64`（expires）。

**Q7.7 ★★** 盘点当前日志的泄露面：AUDIT 行、签发日志、异常日志各包含什么？密码会出现在日志里吗？
代码定位：`src/NitroGateway.Security/Audit/AuditMiddleware.cs:42-50`；`src/NitroGateway.Security/Auth/TokenGenerator.cs:37,45,66`；`src/NitroGateway.Security/Audit/ExceptionHandlingMiddleware.cs:27`。

**Q7.8 ★★★** 审计日志可以被攻击者刷爆吗？现在哪个接口有限流？如果要给全 API 加频控，ASP.NET Core 里用什么？
代码定位：`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:97-98`（仅登录限流）。

---

## 八、开放设计（追问/演进）

**Q8.1 ★★★** 用户放在配置文件而不是数据库，优劣势？如果产品要支持「动态增删用户 + 改密码」，需要动哪些模块？
代码定位：`src/NitroGateway.Security/Auth/UserConfig.cs:3`（类注释：用户量小，配置文件管理即可）。

**Q8.2 ★★★** 为什么不引入 refresh token？8 小时 access token 的风险窗口有多大？引入 refresh token 的代价是什么？
代码定位：`src/NitroGateway.Security/Auth/JwtConfig.cs:18`。

**Q8.3 ★★★** 当前只有一个对称密钥，如何做密钥轮换？轮换时已签发 token 会怎样？正确做法（多 key + kid）需要改哪里？
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:49-50`；`src/NitroGateway.Security/SecurityServiceCollectionExtensions.cs:54-68`。

**Q8.4 ★★** 三个校验器拆成独立 FluentValidation 类再组合，而不是一个巨型校验器，设计上的理由？
代码定位：`src/NitroGateway.Security/Guard/WriteGuard.cs:29-57`。

**Q8.5 ★★★** 登录限流从内存版升级为 Redis 版，接口要怎么设计才能让调用方（AuthController）无感替换？
代码定位：`src/NitroGateway.Security/Auth/LoginRateLimiter.cs:28-63`（三个公开方法）。

**Q8.6 ★★★** 审计目前走 ILogger/Serilog 落文件。如果要支持「按用户/时间段查询审计」的合规需求，方案是什么？代价呢？
代码定位：`src/NitroGateway.Security/Audit/AuditMiddleware.cs:42-50`；`src/NitroGateway.Webapi/appsettings.json:16-29`（Serilog 落点）。

---

## 九、动手实验（跑起来验证）

**Q9.1 ★** 启动 Webapi，用 admin/admin123 登录拿 token，把 token 的 payload 段 base64url 解码，验证里面的 claims 和 exp。
代码定位：`src/NitroGateway.Webapi/Controllers/AuthController.cs:23`。

**Q9.2 ★★** 无 token 访问 `GET /api/devices`；再用 viewer 账号（明文密码未公开，可用 Q9.4 程序生成哈希临时替换配置）访问告警确认接口，分别观察 401 和 403。
代码定位：`src/NitroGateway.Webapi/Controllers/AlarmsController.cs:42`。

**Q9.3 ★★** 用错误密码连续登录 5 次，第 6 次应返回 429 和剩余秒数；60 秒后再试错误密码，观察 401 与「立即再次上锁」的再锁行为。
代码定位：`src/NitroGateway.Webapi/Controllers/AuthController.cs:34-45`。

**Q9.4 ★★★** 写一个 5 行小程序用 `PasswordHasher<UserConfig>` 生成 admin123 的哈希，替换 appsettings 里 admin 的 Password：验证「哈希可登录、明文不可登录」两种配置。
代码定位：`src/NitroGateway.Security/Auth/TokenGenerator.cs:16,42`。
