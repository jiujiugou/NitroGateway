# ADR-022: Webapi 模块 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施；P3-6 metrics 决策保留

## Context

Webapi 全量 review 发现：Hub 未鉴权、Viewer 可写、maxCount 未夹紧、部署地址不可达、JWT 回退公开占位符、非法输入 500、历史无分页、客户端可控 ID、审计记高频 GET、Swagger/metrics 生产暴露、Forwarder 间隔硬编码。

## Decision

- D1 LiveDataHub 鉴权：[Authorize] + MapHub(...).RequireAuthorization() + SubscribeDevice/UnsubscribeDevice 校验 Guid；前端已传 access_token，兼容。
- D2 RBAC：全部 POST/PUT/DELETE 动作加 [Authorize(Roles=AdminOperator)]，GET 保持 AllRoles；授权反射测试锁定。
- D3 DeadLettersController maxCount 夹紧：Math.Clamp(maxCount, 1, 1000)，防 SQLite LIMIT 负值全表返回。
- D4 部署可达：Dockerfile 运行时注入 ASPNETCORE_URLS=http://0.0.0.0:5100（appsettings Urls=localhost 致 Docker 不可达）。
- D5 JWT 回退防护：compose 改 ${JWT_SECRET:?必填} + AddNitroSecurity 拒绝含 ChangeMe 占位符启动（fail-fast）。
- D6 非法输入 400：Devices/AlarmRules 枚举与 Guid 全改 TryParse + BadRequest；Create/TestConnection 空嵌套保护。
- D7 AlarmsController.History 分页：IAlarmRepository.QueryAsync 加可选 limit（接口只增不删），实现 clamp + Take。
- D8 创建路径服务端生成 ID：POST 创建（Create/AddPoint）一律 Guid.NewGuid()，忽略客户端 Id。
- D9 审计日志分级：只读 GET 降 Debug，写操作保持 Information/Warning。
- D10 Swagger 仅 Development 启用；/metrics 维持无鉴权（Prometheus 抓取契约，决策保留）。
- D11 Forwarder 间隔可配：新增 Forwarder:IntervalMs 配置（默认 5000）。
- D12 服务定位改构造注入：TestConnection/串口接口改构造注入（IProtocolDriverFactory/ISerialPortManager）。

## 复核撤回

- P3-4 异常响应 JSON 契约不一致：复核确认 AddControllers() 默认 System.Text.Json camelCase 与 ExceptionHandlingMiddleware 匿名对象均为 camelCase，两侧契约实际一致，撤回该条。

## Rationale

- 鉴权/RBAC 收口安全面；输入校验/夹紧防 400/500 与越界；部署与 JWT 防生产不可用；审计分级防高频 GET 刷日志；metrics 公开为抓取契约。

## Consequences

- Hub 与写操作受 RBAC 保护；非法输入返回 400；Docker 内可达；生产缺 JWT/占位符拒启；审计日志不含高频 GET；/metrics 无鉴权维持。
