# ADR-022: Webapi 模块 Code Review

- 日期: 2026-08-09
- 状态: 全部条目已处理（2026-08-09），除 P3-6 metrics 决策保留
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除对应条目
- 范围: src/NitroGateway.Webapi 全部源文件 + Program.cs 接线 + 消费的 Security/Storage/Alarm 接口 + 部署配置（appsettings.json / docker-compose.yml / launchSettings.json）

## 处理记录（2026-08-09）

### P1 安全 / 部署缺陷
- P1-1 LiveDataHub 未鉴权: `[Authorize]` + `MapHub(...).RequireAuthorization()` + SubscribeDevice/UnsubscribeDevice 校验 Guid；前端已传 access_token，兼容
- P1-2 DevicesController Viewer 可写: 全部 POST/PUT/DELETE 动作加 `[Authorize(Roles=AdminOperator)]`，GET 保持 AllRoles；授权反射测试锁定
- P1-3 DeadLettersController maxCount 未夹紧: `Math.Clamp(maxCount, 1, 1000)`，防 SQLite LIMIT 负值全表返回
- P1-4 appsettings Urls=localhost 致 Docker 不可达: Dockerfile 运行时注入 `ASPNETCORE_URLS=http://0.0.0.0:5100`
- P1-5 docker-compose JWT 回退公开占位符: compose 改 `${JWT_SECRET:?必填}` + `AddNitroSecurity` 拒绝含 `ChangeMe` 占位符启动（fail-fast）

### P2 正确性 / 性能
- P2-1 非法枚举/空嵌套输入抛 500: Devices/AlarmRules 枚举与 Guid 全改 TryParse + BadRequest；Create/TestConnection 空嵌套保护
- P2-2 AlarmsController.History 无分页上限: `IAlarmRepository.QueryAsync` 加可选 limit（接口只增不删），Sqlite/InMemory 实现 clamp + Take；控制器传参
- P2-3 OutboxConsumer 热路径日志刷屏: 发送日志降 LogDebug（与 OnStoredAsync 粒度一致）
- P2-4 POST 客户端可控 ID + upsert: 创建路径（Create/AddPoint）一律服务端 Guid.NewGuid()，忽略客户端 Id

### P3 可维护性 / 一致性
- P3-1 AlarmRulesController 未用 `_devices` 依赖: 删除
- P3-2 DevicesController 服务定位: TestConnection/串口接口改构造注入（IProtocolDriverFactory/ISerialPortManager）
- P3-3 LoginRateLimiter 条目永不淘汰: 条目数达上限时清理窗口过期记录，新增 `Count` 诊断属性与测试
- P3-5 审计日志记录高频轮询 GET: AuditMiddleware 只读 GET 降 Debug，写操作保持 Information/Warning
- P3-6 Swagger/Metrics 生产暴露: Swagger 仅 Development 启用；`/metrics` 维持无鉴权（Prometheus 抓取契约，决策保留，不修）
- P3-7 Forwarder 间隔硬编码: 新增 `Forwarder:IntervalMs` 配置（默认 5000），Program.cs 读取
- P3-8 launchSettings 端口不一致: applicationUrl 改 `http://localhost:5100`，与 AGENTS.md/docs 对齐
- P3-9 DeviceStatusDispatcher 健康变更日志无设备信息: 日志带 DeviceId/OldStatus/NewStatus

## 复核撤回
- P3-4 异常响应 JSON 契约不一致: 复核确认 `AddControllers()` 默认 System.Text.Json Web 序列化（camelCase），
  `ApiResponse` 输出与 `ExceptionHandlingMiddleware` 匿名对象（小写属性名）均为 camelCase，前端亦按 camelCase 消费——
  两侧契约实际一致，撤回该条

## 验证
- build 0 错误；UnitTests 234 通过（215+19）；IntegrationTests 40 通过
- 新增: `WebapiAuthorizationTests`（hub 鉴权 + RBAC 反射）、`WebapiControllerTests`（maxCount 夹紧 / 非法输入 400 / 忽略客户端 ID）、`LoginRateLimiterTests` 淘汰用例、`SqliteAlarmRepositoryTests` limit 用例
- 未提交: git 提交由用户执行
