# ADR-004: Security 模块优化决策（边缘网关适配范围）

- 日期: 2026-08-07 | 状态: 已实施

## Context

Security 模块存在写指令门控未接线、密码明文回退、登录无限流、JWT 配置无校验、审计异常丢失等问题；范围限定为边缘网关适配所需，不做用户管理系统/OAuth/SSO/密码过期等超范围设计。

## Decision

- D1 写指令门控为预留能力：网关当前无写操作 API，不接线；注册处注释标记「写门控为预留能力」+ 功能清单同步标注；后续需下发控制指令时另起任务接入 WriteGuard。
- D2 密码只用哈希校验：TokenGenerator.IssueToken 移除明文 Equals 兜底；UserConfig.Password 注释「哈希存储」；appsettings 默认账号密码为哈希（明文口令 admin/admin123、operator/oper123、viewer/view123）。
- D3 登录限流：LoginRateLimiter 内存实现，按「用户名|IP」计数，默认 5 次/10 分钟窗口触发 60 秒锁定；锁定返回 429 + 剩余秒数。
- D4 JWT 配置 fail-fast：JwtSecretKey 字节数 ≥32、ExpireHours ≥1，违规启动抛 InvalidOperationException。
- D5 角色启动校验：UserConfig.Role ∈ {Admin, Operator, Viewer}。
- D6 审计异常兜底：ExceptionHandlingMiddleware 未处理异常统一转 500 JSON，注册在 AuditMiddleware 内层，异常先转 500 再被外层审计记录。
- D7 审计刻意不记请求体（敏感数据），注释说明决策。

## Alternatives

- D3 备选：无登录限流（简单但易被暴力破解）；分布式限流（超范围）。
- D7 备选：记录 body（便于排查，但泄露敏感数据）。

## Rationale

- 边缘网关适配范围最小化；哈希校验杜绝明文凭据；限流/校验/兜底提升安全与可用性；审计不记 body 遵循最小化原则。

## Consequences

- 默认测试口令仅存在于本地哈希配置；登录暴力尝试被限流；配置错误启动即暴露；异常统一 500 JSON 且审计不丢；审计日志不含请求体。
