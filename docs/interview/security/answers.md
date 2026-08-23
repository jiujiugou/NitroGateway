# Security 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：注释/ADR 描述以代码为准；测试在 `tests/NitroGateway.UnitTests`（SecurityConfigValidationTests / TokenGeneratorTests / WriteGuardTests）。

---

## 一、模块总览与 DI 注册

**Q1.1 AddNitroSecurity 的步骤**
①绑定 `Security` 节到 JwtConfig（缺省 `new JwtConfig()`）；②开发密钥降级：密钥空白或以 `NitroGateway-Dev` 开头 → 生成随机 64 hex 密钥；③fail-fast：密钥 UTF8 ≥ 32 字节、ExpireHours ≥ 1、角色 ∈ {Admin, Operator, Viewer}；④注册 JwtConfig / IReadOnlyList\<UserConfig\> / TokenGenerator（均 Singleton）；⑤`AddAuthentication + AddJwtBearer`：四项验证 + SignalR query token；⑥`AddAuthorization` 三个策略 + WriteGuard 四件套 + LoginRateLimiter。顺序保证「先校验配置，再注册依赖它的服务」。
定位：`SecurityServiceCollectionExtensions.cs:15-101`。

**Q1.2 为什么都是 Singleton**
- JwtConfig：构建后只读（init-only），无状态。
- TokenGenerator：只持有配置 + 用户列表快照 + PasswordHasher（无状态），`IssueToken` 不写共享字段。
- WriteGuard / 三个 Validator：FluentValidation 规则在构造函数定义一次，之后只读。
- LoginRateLimiter：唯一有状态，但用 `ConcurrentDictionary` 保证线程安全。
Singleton 避免每请求重建，代价是必须不可变或线程安全——本项目靠 init-only + ConcurrentDictionary 满足。

**Q1.3 开发密钥降级**
触发：`IsNullOrWhiteSpace` 或 `StartsWith("NitroGateway-Dev")`（appsettings.json:53 的开发占位密钥正是这种前缀）。生成：两个 `Guid.ToString("N")` 拼接 = 64 个 hex 字符 = 32 字节。影响：密钥每次启动随机 → 重启后所有已签发 token 签名校验失败（等同全员下线重登）。注释原意：开发便利，token 不跨重启持久。
定位：`SecurityServiceCollectionExtensions.cs:20-31`。

**Q1.4 fail-fast 的意义**
安全配置错误应「拒绝启动」而非「带病运行」：弱密钥 = 攻击者可伪造任意 token（最严重后果），运行期才发现等于把漏洞当功能跑。启动抛异常把问题暴露在部署/测试阶段，符合 ADR-004 P2-2/P2-3。测试：`SecurityConfigValidationTests`（弱密钥/0 小时/非法角色抛异常，合法配置不抛）。

---

## 二、JWT 与 Token 签发

**Q2.1 配置节与默认值**
绑定 `Security` 节（`JwtConfig.SectionName`）。默认 Issuer = Audience = "NitroGateway"，ExpireHours = 8。appsettings 实际值同默认；密钥为 Dev 前缀占位符 → 触发 Q1.3 降级。生产必须用环境变量覆盖 `Security:JwtSecretKey`（AGENTS.md 雷区）。

**Q2.2 密码验证**
`PasswordHasher<UserConfig>`（ASP.NET Core Identity，PBKDF2，哈希形如 `AQAAAA...`，含版本/盐/迭代信息）。`VerifyHashedPassword` 把存储串按哈希格式解析；明文不是合法哈希 → 解析失败 → `Failed` → 登录失败。ADR-004 P1-2 已删除明文 `Equals` 兜底。测试：`TokenGeneratorTests.IssueToken_PlaintextConfiguredPassword_Fails`。

**Q2.3 Claims**
`ClaimTypes.Name`（用户名）、`ClaimTypes.Role`（角色）、`JwtRegisteredClaimNames.Jti`（Guid）。jti 是 token 唯一标识，为吊销/审计/防重放预留；当前只签发不记录，**没有被消费**——这是已知缺口（联动 Q7.6）。

**Q2.4 签名算法**
HMAC-SHA256（`SecurityAlgorithms.HmacSha256`）+ 对称密钥 `SymmetricSecurityKey(UTF8(JwtSecretKey))`。签发端 `TokenGenerator.cs:49-50` 与验证端 `SecurityServiceCollectionExtensions.cs:54-68` 从同一配置构造，天然一致；密钥泄露 = 可伪造任意 token。

**Q2.5 验证参数**
显式开启：ValidateIssuer / ValidateAudience / ValidateLifetime / ValidateIssuerSigningKey。ClockSkew **未设置** → 取 `TokenValidationParameters` 默认 5 分钟：token 过期后 5 分钟内仍被接受（容忍客户端时钟偏差）。对 8h 有效期影响不大，但严格场景应显式收紧（如 30 秒）。

**Q2.6 签发日志**
成功：Information「Token 签发: 用户 {User}, 角色 {Role}」（用户名+角色，无密码无 token）。失败：Warning「用户不存在」/「密码错误」——**响应统一为「用户名或密码错误」，但日志能区分** → 存在用户名枚举面（内网可接受；公网应统一日志文案）。

---

## 三、登录与限流

**Q3.1 登录流程**
①trim 用户名（ADR-004 P3-2，防首尾空格匹配失败）；②空校验 → 400；③`IsLocked` → 429 + 剩余秒数；④`IssueToken` 失败（用户不存在/密码错误）→ `RecordFailure` + 401；⑤成功 → `Reset` + 200（Token + Bearer）。注意：只有「凭据错误」计数，空参数、429 不计数。

**Q3.2 key = username|IP**
- 只按用户名：同一 IP 可换用户名枚举（每用户名独立计数，各试 4 次），且一个用户被锁影响所有来源。
- 只按 IP：同一出口 IP 的用户互相污染（A 错 5 次锁掉 B）。
- 组合：单用户单来源被锁，其他组合不受影响。缺陷：攻击者换 IP 即绕过；反代后 IP 失真（见 Q7.3）。

**Q3.3 默认参数与窗口**
maxFailures=5、window=10 分钟、lockDuration=60 秒（构造参数可覆盖，`Math.Max(1, maxFailures)` 夹紧）。重置逻辑：`now - FirstFailureAt > _window` → 重新 `Entry(1, now, null)`——窗口从**首次失败**起算，过期后计数清零重来。

**Q3.4 锁定态不累加 + 再锁行为**
若锁定期间继续累加/刷新 LockedUntil，攻击者每次失败都能无限续锁（永久锁定）。返回原 Entry 保证锁在固定 60 秒后到期。**坑**：锁到期后再失败 1 次，因 `failures` 仍 ≥ 5（窗口未过）会**立即再次上锁 60 秒**——「再锁」而非「重新数 5 次」，配合 Q7.4 的 DoS 场景看。

**Q3.5 内存实现边界**
①多实例：每实例独立计数，攻击者可把失败分散到各实例绕过（单实例网关注定无此问题）；②重启：字典清空、计数归零；③`Reset` 只在登录成功时删除 Entry → 长期运行 + 大量失败 key 时字典只增不减（内存增长量级 = 失败 key 数 × Entry 大小，通常可接受，但无主动清理）。类注释声明：**仅作边缘网关内网防暴力破解的最小平，不做分布式/持久化**——这是有意取舍，不是缺陷。

---

## 四、RBAC 与授权

**Q4.1 Roles 常量**
Admin / Operator / Viewer + 组合常量 `AdminOperator`（"Admin,Operator"）、`AllRoles`（"Admin,Operator,Viewer"）。`[Authorize(Roles="A,B")]` 语义是**任一角色即可（OR）**，所以逗号拼接常量可直接用；Viewer 定位只读。

**Q4.2 控制器角色矩阵**
| 控制器 | 角色 |
| --- | --- |
| Alarms（查看） | AllRoles |
| Alarms（确认） | AdminOperator |
| AlarmRules / PointImport | AdminOperator（DeadLetters 控制器已于 2026-08-22 删除） |
| Status / Devices / Measurements | AllRoles |
| Auth（login） | AllowAnonymous |
规律：**读 = 全员；写/变更 = Admin + Operator；Viewer 一律不可变更**。注意 DevicesController 当前只有查询端点，未来加增删改端点时应按操作分级而非沿用类级 AllRoles。

**Q4.3 最小权限**
查看是只读职责，Viewer 需要；确认告警是状态变更（影响告警处置流），只给需要执行的人——最小权限原则（least privilege）。同理 AlarmRules / PointImport 都是「操作」，排除 Viewer（原 DeadLetters 亦同，已随死信删除 2026-08-22）。

**Q4.4 SignalR query token**
浏览器 WebSocket 无法自定义 Authorization 头，SignalR 官方模式是从 query string 读 `access_token`（`OnMessageReceived`）。风险：token 出现在 URL → 浏览器历史、代理/网关访问日志、Referer 泄露面扩大。缓解：短有效期 + TLS + 访问日志脱敏；另外当前实现**对所有请求路径**都读 `access_token`（不限定 SignalR 路径），属于宽松实现。

---

## 五、写保护 WriteGuard

**Q5.1 顺序与短路**
Mode → Range → Rate，任一失败立即 `return` 该结果（短路），全过返回空 `ValidationResult`。Mode 第一：设备不在线是最根本的安全闸（工业安全第一原则——停机设备不接受任何控制指令），且校验最廉价。测试：`WriteGuardTests` 9 场景。

**Q5.2 ModeValidator**
`status is "Online"` **精确字符串匹配**：`"online"`、`"Online "`、`"ONLINE"` 全部失败（消息「设备不在线，无法执行写操作」）。依赖 Device 模块 `DeviceStatus.ToString()` 的规范枚举值。

**Q5.3 RangeValidator**
MinLimit 与 MaxLimit **都为 null** 时跳过（`When` 条件）。边界**包含**：`value < min` 或 `value > max` 才拒绝，等于边界放行。测试：`NoRangeLimit_PassesRangeCheck`、`ValueBelowMin_Rejected`。

**Q5.4 RateLimitValidator**
公式 `change = |(value - prev) / prev| ≤ 1.0`（±100%）。跳过①PreviousValue 为 null：首次写入无参照（`FirstWrite_SkipsRateCheck`）；跳过②`|prev| < 0.001`：防除零与浮点噪声，视为 0 不校验（`PreviousValueZero_SkipsRateCheck`）。

**Q5.5 变化率缺陷**
①**无时间维度**：两次写间隔 1 秒 vs 1 小时判定完全相同，应结合时间窗（如「X 秒内变化 > Y% 拒绝」）；②prev 为负时语义漂移：-50→0 的 `|(0-(-50))/(-50)| = 1.0` 被放行，但绝对值变化达 100%；③近零跳过：0.001→10（+999900%）放行；④不对称：100→0（-100%）与 0→100（无穷）判定不等价。改进方向：对称公式 `|value-prev| / max(|prev|, eps)` + 时间窗 + 点位级速率配置。

**Q5.6 DeviceStatus 默认值陷阱**
是隐患。若接线时调用方不填充真实状态（或信任客户端传入），默认 "Online" 让 Mode 校验直接放行——第一道闸形同虚设。未来接线时 DeviceStatus 必须由服务端从设备管理模块实时取，禁止依赖客户端传入或默认值；更稳的做法是 WriteGuard 内部查设备状态而非信任 DTO。

**Q5.7 未接线现状**
启用三步：①新增写端点（Controller）；②端点内构造 WriteCommand 并调 `WriteGuard.Evaluate`；③经协议驱动（Modbus/S7 `WriteAsync`）落到底层，docs F-28 已同步标注。目前安全是因为**没有写端点 = 攻击面不存在**；但边界是「约定而非强制」——任何新端点忘了调 Guard 就是裸写。接线时应做成统一 WriteService 强制管线，而非靠每个端点自觉。

---

## 六、审计

**Q6.1 记录时机**
`await _next` 之后才有 Response.StatusCode 与真实耗时；之前记录只能拿到「将要发生什么」，拿不到**结果**——审计必须有 Result（Who/What/When/**Result**/IP）。

**Q6.2 /api 过滤与字段**
`/api` 是管理面（登录、设备、告警、点位导入），需要留痕；`/healthz`、`/readyz`、`/metrics`、SignalR 高频非管理操作不审计，防刷屏。字段：user（Name claim，匿名 = "anonymous"）、role（"-"）、method、path、statusCode、elapsedMs、IP。

**Q6.3 日志分级**
≥400 → Warning：失败请求（攻击探测、鉴权失败、参数错误）从日志级别直接可过滤，Warning 本身就是告警通道；正常请求 Information 不吵。运维可直接 `grep Warning` 看异常活动。

**Q6.4 不记 body**
写类操作的变更内容是敏感数据（工艺参数、控制指令），落盘 = 扩大泄露面（ADR-004 P3-3）。当前取舍：method/path/status 足以追溯「谁做了什么操作」，不记「做成什么样」。若需 body 摘要：请求前 `Request.EnableBuffering()`，限量读取（如 1KB）并脱敏。

**Q6.5 中间件顺序（关键）**
`Program.cs:112-113`：Audit 在外（先注册）、Exception 在内（后注册）。若颠倒：端点抛异常时，Exception 捕获并写 500，但异常从 Audit 的 `await _next` 抛出 → **Audit 记录代码不执行 → 该请求完全无审计记录**，且审计永远看不到异常转出的 500。当前顺序保证：异常先被内层转成 500 响应，外层 Audit 正常记录真实状态码（类注释「必须注册在 AuditMiddleware 内层」，ADR-004 P2-4）。

**Q6.6 HasStarted rethrow**
`HasStarted = true` 表示响应已开始发送（可能已写部分字节），此时 `Clear()`/改状态码会破坏已发响应甚至协议错误；唯一合理动作是 rethrow 交给服务器中止连接（客户端拿到不完整响应，但不会收到错乱的 200+500 混合体）。

---

## 七、攻防场景

**Q7.1 伪造 role**
签名校验失败：篡改 payload 后 HMAC-SHA256 签名不匹配 → JwtBearer 认证失败 → **401**。没有密钥无法伪造；前提是密钥强度与保密（联动 Q7.5）。

**Q7.2 401 vs 403**
**403**：token 有效（已认证）但角色不满足 → AuthorizationMiddleware 返回 Forbidden；401 专指未认证（无 token / token 无效 / 过期）。

**Q7.3 限流绕过与反代**
路径：①换 IP（key 含 IP，每 IP 5 次额度）；②每用户名只试 4 次（key 含用户名，N 个用户名 × 4 次）；③多实例轮询（各实例独立计数）。反代部署：`RemoteIpAddress` = **代理 IP** → 所有用户共享同一 key：A 用户连续失败会锁掉同代理下的 B（误伤），且无法区分真实来源。缓解：`UseForwardedHeaders` 信任代理取 X-Forwarded-For、IP 全局 + 用户全局双维度计数、指数退避。

**Q7.4 锁死 admin 的 DoS**
是低烈度 DoS：5 次错误请求即可锁 admin 60 秒；配合 Q3.4 的「再锁行为」（锁到期后再错 1 次立即续锁）可**无限续锁**。缓解：锁定期指数递增、达阈值后要求验证码、按 IP 叠加全局限制、审计日志告警（429/Warning 已可观测）。内网单用户锁 60s 影响可控——这是类注释声明过的取舍。

**Q7.5 Dev 前缀密钥进生产**
降级分支**不报错**：prod 配置 Dev 前缀密钥 → 启动生成随机密钥 → 所有客户端 token 重启即失效 → 表现「登录成功但马上 401」（旧 token 签名全挂），运维难以排查；密钥为空同理。与 fail-fast 的冲突：fail-fast 只查**长度**（≥32 字节）没查**前缀**。改进：生产环境把 Dev 前缀视为错误直接拒绝启动，或按环境（Development/Production）分流——这是本模块最值得提的改进点。

**Q7.6 token 吊销**
现状：无吊销（jti 只签发不记录）、无刷新机制，8h 内 token 均有效。方案①缩短 ExpireHours（代价：频繁重登）；②维护 jti 黑名单（内存/Redis；代价：回到有状态，验证管线加一步）；③轮换 JwtSecretKey（全员失效；代价：粗暴、影响所有在线用户）；④用户级 token 版本号（需数据库用户表，联动 Q8.1）。

**Q7.7 日志泄露面**
AUDIT：用户名/角色/IP/method/path/状态/耗时——无 body、无密码；签发日志：用户名+角色（成功）与「用户不存在/密码错误」区分（失败）→ **用户名枚举面**；异常日志：异常类型 + 堆栈 + 路径（可能含内部结构）。密码与 token 本体不会出现在代码日志中；例外：SignalR `access_token` 若被代理/访问日志记录属部署层风险（Q4.4）。

**Q7.8 审计刷屏**
能。目前**只有登录限流**（`LoginRateLimiter`），其余 `/api` 无频控；循环调用即可刷审计与 Serilog 文件（磁盘/CPU DoS，且真实攻击留痕被淹没）。缓解：ASP.NET Core 内置 RateLimiter 中间件（固定窗口/滑动窗口/令牌桶）按 IP + 用户限流、审计日志采样/配额。

---

## 八、开放设计

**Q8.1 配置文件用户 vs 数据库**
配置文件：零基础设施、改配置重启生效，适合用户量小且固定的工业网关（类注释明说）；缺：无增删改查、密码轮换要动文件。动态用户演进路径：Domain 新增用户实体（Domain 不引用基础设施）→ Persistence 迁移 + EF 仓储 → Storage 按「接口只增不删」加 IUserStore → TokenGenerator 改为查用户存储 → Webapi 用户管理端点（Admin 保护）。

**Q8.2 refresh token 取舍**
8h 窗口 = token 泄露后的有效攻击窗口。refresh token 可把 access 压到分钟级，且 refresh 可吊销、可轮换；代价：需要存储（DB/Redis 存 refresh 哈希）、刷新端点、轮换与重放处理、前端改动。用户量 <10 的内网网关「8h 够用」是合理取舍；上云/公网必须引入。

**Q8.3 密钥轮换**
当前单密钥：直接换 → 全部 token 失效（全员重登）；不换 → 泄露密钥不可恢复。正确做法：多密钥列表 + 签发时带 `kid`（KeyId header），验证按 kid 选 key，旧 key 保留到所有旧 token 过期后移除（宽限期）。改动点：TokenGenerator 签发加 kid、`TokenValidationParameters.IssuerSigningKeys` 传密钥数组、配置支持密钥列表。

**Q8.4 独立 Validator 组合**
单一职责（一个校验器一个关注点）+ 独立单测（`WriteGuardTests` 9 场景）+ 组合顺序由 WriteGuard 显式编排（可读、可调序、可扩展）+ FluentValidation 惯例。巨型校验器：规则耦合、测试面爆炸、复用困难。

**Q8.5 限流抽象**
把 LoginRateLimiter 抽象为 `ILoginRateLimiter`（IsLocked / RecordFailure / Reset），内存与 Redis 实现各自实现，AuthController 只依赖接口。Redis 实现：INCR + EXPIRE 做窗口计数、SET NX EX 做锁，注意原子性与时钟问题。当前类 sealed 未抽接口——这正是「接口只增不删」文化的正面案例（先抽象再扩展）。

**Q8.6 审计可查询化**
方案①保留 Serilog 文件 + 引入日志查询（Loki/ELK）——低成本、链路不变；②审计落 SQLite 表（复用 Persistence）——可按用户/时间/路径 SQL 查询，代价：每请求一次写（需批量/异步队列削峰）、迁移、保留策略；③折中：敏感操作（登录/写操作/权限变更）入表，其余留日志——工业网关典型选③。

---

## 九、动手实验

**Q9.1 登录与解码**
`POST http://localhost:5100/api/auth/login`，body `{"username":"admin","password":"admin123"}` → 取 `data.token`；把 token 按 `.` 切分取第 2 段，`-`/`_` 替换为 `+`/`/` 并补 padding 后 base64 解码，可读 claims：`unique_name`(admin)、`role`(Admin)、`jti`、`iss`/`aud`(NitroGateway)、`exp`(8h 后)。

**Q9.2 401 vs 403**
无 token `GET /api/devices` → 401（未认证）。viewer 明文密码未公开：用 Q9.4 程序生成哈希临时替换 appsettings 的 viewer 行 → 重启 → viewer 登录 → 调 `POST /api/alarms/{id}/confirm` → 403（角色不足）；admin 调同一接口才有机会成功。

**Q9.3 限流与再锁**
错误密码连试 5 次：第 5 次失败后锁定；第 6 次 → 429 + 「请 X 秒后再试」。60 秒后错误密码 → 401（不再 429）；但**这次失败会立即再次上锁 60 秒**（Q3.4 再锁行为），可顺手验证。

**Q9.4 哈希验证**
临时 dotnet 控制台（或测试）：`new PasswordHasher<UserConfig>().HashPassword(null!, "admin123")` → 输出替换 `Security:Users:0:Password` → 重启后 admin/admin123 登录成功；再把配置改成明文 `"admin123"` → 重启 → 登录失败（验证 Q2.2 哈希格式解析）。
