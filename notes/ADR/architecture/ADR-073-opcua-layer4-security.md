# ADR-073: OPC-UA 连接安全与证书白名单封装（层 4 · P0-2 + P2-1）

- 日期: 2026-09-02 | 状态: 决策已定（文档定稿；实现与验收待执行，见 AC-opcua-layer4-security）
- 来源: docs/07-OPC-UA四层生产化封装审查与实施计划.md 层4（§2 能力对照表行 119-129、§4 P0 ②/
  P2、§5 W4 证书白名单 XL=324 + W5 凭据/策略可配置 M=72、§6 验证方案）；SDK 1.5.378.156
  包内 XML 与 OPC Foundation 源码复核；ADR-072（层3 会话自愈，本层在其上扩展安全档位与
  UserIdentity，仅动建连前段，不动自愈/订阅）；ADR-019（驱动 `_gate` 串行、不产伪值）；
  12-OPC-UA接入设计.md（Protocol/OpcUa 模块边界）

## Context

层 4 审查结论：`OpcUaDriver` 的现状是"内网演示可用、生产不可用"，四项生产化缺口全部在此：

- **应用证书尽力而为（P2-1a）**：`CheckApplicationInstanceCertificates(silent: true)` 被 try/catch
  包住，失败即置 `_hasAppCertificate=false` + `LogDebug`（`OpcUaDriver.cs:110-125`）→ 静默降级
  None+匿名，目录只读/生成失败无任何用户可见错误。
- **信任列表空白（P2-1b）**：`AutoAcceptUntrustedCertificates=true`（`OpcUaDriver.cs:977`）且
  `config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true`（`:108`），
  任何自签服务器证书都被无校验接受。
- **信任管理无操作入口（P2-1c）**：rejected 证书只能看 pki 文件，无"信任→重试"路径。
- **策略/模式自动选 + 硬回退 None（P0-2a/b）**：`SelectEndpoint(useSecurity: _hasAppCertificate)`
  catch 后无条件回退 `useSecurity:false`（`:129-137`），即"选不到安全端点就静默连 None"。
- **身份匿名硬编码（P0-2c）**：`Session.Create(..., new UserIdentity(), ...)`（`:149`），
  `DeviceConnection.Parameters` 里的用户名/密码完全未用。

风险判定：W5（凭据/策略）2×3×3×2×2=72 → M；W4（证书）3×3×4×3×3=324 → XL（先降险再动工，
拆子步骤：凭据/策略可配置 → AutoAccept 关闭 → 信任管理流程，每步独立验证）。docs/07 §6 验证
方案要求"None/加密端点两套；首连证书拒绝→信任→成功；用户名密码对/错"。

SDK 能力复核（决策依据）：

- `CoreClientUtils.SelectEndpoint`（SDK 1.5.378.156）仅有三个重载：
  `(ApplicationConfiguration, ITransportWaitingConnection, bool[, int])`、
  `(ApplicationConfiguration, string, bool[, int])`、
  `(ApplicationConfiguration, Uri, EndpointDescriptionCollection, bool, ITelemetryContext)`。
  **不存在可传 `SecurityPolicyUri` 过滤的 `SelectEndpoint` 重载**——docs/07 表格括号注
  "可传 SecurityPolicyUri 过滤"不准确。按策略/模式选端点必须自己走
  `DiscoveryClient`/`GetEndpoints` 拉取 `EndpointDescriptionCollection` 后，用
  `EndpointDescription.SecurityPolicyUri` / `.SecurityMode` / `.SecurityLevel` 手工过滤
  （本 ADR 按 ADR-072 更正 `BeginReconnect` 参数的同一纪律予以更正）。
- `UserIdentity` 构造齐全：`()`（匿名）、`(string, byte[])`、
  `(string, ReadOnlySpan<byte>)` 等——用户名密码可直传 byte[] 进 `UserNameIdentityToken`，
  无需手写 token。
- 证书管理能力齐全：`CertificateValidator`（`CertificateValidation` 事件、`Update`）、
  `CertificateTrustList`（`GetCertificates`/`TrustedCertificates`）、
  `CertificateStoreIdentifier.OpenStore()`、`ICertificateStore`
  （`EnumerateAsync/AddAsync/DeleteAsync/AddRejectedAsync`），配合目录类存储即可实现
  rejected→trusted，不必手写证书解析/落盘。

凭据落盘复核（"写 ADR"的触发点，docs/07 层4 行 129）：

- `DeviceConnection.Parameters` 是 `Dictionary<string, object>`（`Domain/Devices/DeviceConnection.cs`）；
  `Persistence/DomainMapper.cs` 把整个字典以 JSON 直存 `devices.ConnectionParams` 列。
  若用户名/密码直接进 Parameters，会**明文落 SQLite**。
- 仓库唯一可逆加密先例在 Desktop：`DpapiProtector`（DPAPI CurrentUser，`crypt32.dll`，
  Windows only）——Linux Docker 生产不可照搬；`PasswordHasher` 是单向哈希，不适合存需回读的
  密码。仓库暂无跨平台可逆加密服务。
- 环境变量注入是既有先例：`.env.example`/`docker-compose.yml` 以 `JWT_SECRET`/`ADMIN_PASSWORD`
  → `Security__JwtSecretKey`、`Security__Users__0__Password` 注入并 fail-fast 拒启。
- 模块边界：`Protocol/OpcUa/NitroGateway.Protocol.OpcUa.csproj` 只引用 Abstraction+Domain，
  **不能引入 `NitroGateway.Security`**（Security 属 Webapi/Persistence 层）——加解密不得发生在
  Protocol 模块内。

## Decision

- D1 **安全参数契约与校验（P0-2a/b/c 读参口径）**：`OpcUaDriver` 从
  `_connection.Parameters` 读取（键名精确，沿用现有 PascalCase 参数字典约定）：
  `SecurityPolicy`（取值：`None`，或 SDK `SecurityPolicies` 常量对应名如
  `Basic128Rsa15`/`Basic256`/`Basic256Sha256`）、`SecurityMode`
  （取值：`None`/`Sign`/`SignAndEncrypt`）、`UserName`、`Password`。
  键存在但值为空串/类型错误/非法枚举 → `OperationResult.Validation`（400），**绝不 500**
  （W5 约束卡：`Parameters` 空值校验 400 而非 500）；这些键只在 OPC UA 协议连接时消费，
  Modbus/S7 驱动忽略（各自协议参数互不污染）。
- D2 **端点选择：显式策略过滤 + 删除隐式 None 回退（P0-2a/b）**：建连前用
  `DiscoveryClient`/`GetEndpoints` 拉取端点一次，按 `SecurityPolicy`+`SecurityMode` 手工过滤
  （SDK `SelectEndpoint` 无策略重载，见 Context 更正）。无匹配端点 → `OperationResult.Validation`
  并附可用端点清单（策略/模式/SecurityLevel），便于用户改配。**删除 `OpcUaDriver.cs:129-137`
  的 catch 后无条件回退 None**。未声明任何策略/模式时的默认 = **安全优先**：选非 None 中
  `SecurityLevel` 最高且服务端证书受信任的端点；若既未显式声明 None、又无任何可用安全端点 →
  `ValidationError`（提示"目标仅提供 None，须显式配置 SecurityPolicy=None"）。
- D3 **None 仅显式配置才允许**：`SecurityPolicy=None` 或 `SecurityMode=None` 二者任一显式
  声明时才允许选中 None 端点（docs/07 W4/W5 约束卡与 §6 均要求此行为）。无任何隐式路径
  自动落到 None。
- D4 **身份与凭据（P0-2c）**：`UserName` 与 `Password` 均配置 →
  `new UserIdentity(user, Encoding.UTF8.GetBytes(password))`；`UserName` 配置而 `Password`
  缺失/空 → `ValidationError`（防"以为有认证实为匿名"的误配，W5 空值校验）；两者均未配置 →
  `new UserIdentity()` 匿名（仅当服务端所选端点允许匿名，由握手失败显式报错）。密码只在
  建会话瞬间进入 `UserNameIdentityToken`，之后由 SDK 管理，驱动不二次持有。
- D5 **凭据落盘与传输（层4 明示写 ADR 点）**：
  - 设计目标：明文密码只存在于"前端输入 → API 请求 → 宿主内存 DTO → 建会话"的瞬时链路；
    **不落 `appsettings.json`、不落 SQLite `ConnectionParams` 明文、不落日志**。
  - 持久化：`devices.ConnectionParams` 只落 `UserName` 与 `Password` 的**密文**（AES-256-GCM，
    含算法/盐/IV 元数据），`Password` 明文键永不出现在序列化 JSON。主密钥经宿主环境变量注入
    （与 `JWT_SECRET`/`ADMIN_PASSWORD` 同模式，如 `OPCUA_CREDENTIAL_KEY`，示例进
    `.env.example`/`docker-compose.yml`），密钥不进 DB/appsettings。密钥缺失时凭据解密路径
    fail-fast，禁止以"明文回写"兜底。
  - 解密边界：加解密助手 `ICredentialProtector`（Protect/Unprotect，接口形态对齐 Desktop
    `DpapiProtector`，实现为跨平台的 AES-GCM + 环境变量密钥）放在宿主侧（Webapi/Persistence）；
    只有**组装 DeviceConnection 供驱动连接的时刻**（测试连接端点 / 驱动池建驱动前）才解密为
    内存明文传给 `OpcUaDriver`。Protocol 模块只消费内存明文 Parameters，**不引入任何加解密
    依赖**（模块边界硬约束）。
  - 前端：`DeviceForm.vue` 增加 OPC UA 安全区（协议=OPC UA 时显示）；密码框 `type=password`
    **不回显**，编辑态回填只给掩码/占位（如 `••••••••` + "留空则不修改"），保存时留空 =
    沿用既有密文不覆盖。`GET /api/devices` 响应不返回明文密码（仅 `hasPassword` 标志）。
- D6 **关闭 AutoAccept + 校验回调只记录拒绝（P2-1b）**：`BuildConfiguration` 置
  `AutoAcceptUntrustedCertificates=false`（去掉 `OpcUaDriver.cs:977` 的 `true`）；删除
  `:108` 的 `CertificateValidation += (_, e) => e.Accept = true`。保留/改写的
  `CertificateValidation` 回调**只记录拒绝原因**（含 Subject/Thumbprint/时间/错误码），
  **禁止 `e.Accept = true`**；未受信任证书由 SDK 判 `BadCertificateUntrusted` → 连接以
  明确证书错误失败，前端提示可"信任此服务器证书"。
- D7 **应用证书失败不再静默（P2-1a）**：`CheckApplicationInstanceCertificates` 失败（目录
  不可写/生成失败）不再 catch 后置 `_hasAppCertificate=false` 降级 None，改为抛出/返回明确
  `SecurityConfigurationError`（含原因），连接失败并向前端呈现；成功生成才走加密端点。
  `OpcUaDriver.cs:31-34` 类注释与 `BuildConfiguration` 的"生产应改为信任库白名单"表述同步
  更新为已落地的白名单语义。
- D8 **证书管理与信任流程（P2-1c）**：在 Webapi 增加证书管理服务 + API（前端证书面板）：
  - 读 rejected 证书列表（Subject/Thumbprint/导入时间），来源即
    `BuildConfiguration` 的 `RejectedCertificateStore`（`opcua/pki/rejected` 目录）；
  - "信任此服务器证书"= 把该证书从 rejected 移入 `TrustedPeerCertificates`（`opcua/pki/trusted`，
    目录类存储；SDK `CertificateTrustList` 目录语义）；支持撤销（trusted→移除）作为运维操作；
  - 信任后触发该设备重试连接。
  信任状态属**文件系统 PKI 状态**，不入 SQLite 设备表（避免与 pki 目录双写漂移，pki 目录是
  唯一权威）；现场互认流程（首次上电加证书、跨站点证书分发、到期轮换）文档化为运维交付项，
  不写进代码。
- D9 **安全测试基建（docs §6）**：扩展/新增进程内安全参考服务器 scope，同时暴露
  **加密端点（Basic256Sha256 + SignAndEncrypt + UserName 策略）与 None 端点**两套，供集成
  用例覆盖：None/加密两套均显式配置可达、首连证书拒绝→信任→成功、用户名密码对/错、
  无隐式回退。安全用例不依赖 DataChange 通知，不受层2/层3 已知测试服务器通知限制影响。

## Alternatives

- A. 保持现状（AutoAccept + 匿名 + 静默 None）：演示/内网可用，生产可被伪造服务器中间人
  窃听；用户名密码一旦入 Parameters 即明文落 DB，违背凭据安全硬约束，否决。
- B. 按 docs/07 原表述调用"可传 SecurityPolicyUri 的 `SelectEndpoint`"：SDK 1.5.378.156 无此
  重载，编译不过或退化为布尔开关，无法按策略过滤 → 更正为 GetEndpoints + 手工过滤，否决。
- C. 照搬 Desktop `DpapiProtector`（DPAPI CurrentUser）：Windows only，Linux Docker 生产不可用，
  否决；改为跨平台 AES-GCM + 环境变量注入主密钥（"机器级密钥"的生产等价实现）。
- D. 密码明文存 Parameters（现状容器语义）：直接违反"密码不得明文落 appsettings/DB"，
  否决。
- E. 密码不持久化（每次重录/仅内存）：破坏重启后自动重连与测试连接闭环，且易"漏录→静默匿名"，
  否决。
- F.（选定）显式安全档位（策略/模式/None 显式）+ 凭据加密落库（env 注入主密钥）+ 白名单
  校验（关 AutoAccept、明确拒绝码）+ rejected→trusted 管理入口：全部复用 SDK 目录信任/
  CertificateValidator 能力，新增的是配置口径、错误码、凭据保护助手与管理 API/UI。

## Rationale

- 复用优先（docs/07 §3）：白名单校验、rejected 收集、目录信任都是 SDK
  `CertificateTrustList`/`CertificateValidator`/`ICertificateStore` 已实现能力；"要写的只有
  业务映射、错误码、管理入口、配置与 UI"——证书文件读写/校验逻辑不得手写第二套。
- None 仅显式：与 W4/W5 约束卡、§6 验证方案一致；把"安全做错 = 全连不上（证书卡死）或
  不安全（静默 None）"的二元风险收敛为"显式声明 + 明确错误码"，误配暴露为可修的 400/明确
  连接错误，而非静默降级。
- 配置错误 400 而非 500：空值/非法枚举是用户可修输入问题，不是服务端故障；走既有
  `OperationResult.Validation` 契约，不吞成 500。
- 密码不落明文是硬约束且 UI 配置型凭据必须能自动重连 → 唯一正解是加密落库；主密钥环境变量
  注入沿仓库既有 secrets 模式（`JWT_SECRET`/`ADMIN_PASSWORD` fail-fast），跨平台、可审计。
- 解密边界后移：Protocol 模块只消费内存明文，避免 OPC UA 驱动依赖宿主密钥管理，保住
  `Protocol/OpcUa.csproj` 只引 Abstraction+Domain 的既有边界；明文生命周期最短。
- 事实更正入档：`SelectEndpoint` 无策略过滤重载（docs/07 表述有误），与 ADR-072 更正
  `maxRetries` 同一纪律，避免实现者传不存在的参数。

## Consequences

- 生产正式获得"加密端点 + 用户名密码 + 白名单校验"的可配置连接路径：目标服务器若同时提供
  Basic256Sha256+SignAndEncrypt 端点，首次连接会先被拒绝（`BadCertificateUntrusted`），经
  证书面板"信任"后重连成功；None 端点只在显式声明时可选。连接安全档位由设备配置唯一决定，
  与 UI 显示一致。
- 兼容/迁移：当前驱动从不落 UserName/Password（恒匿名、参数未用），**无存量明文密码需迁移**；
  真正的影响是"曾自动连 None 的历史无参数设备"，若其目标服务器仅提供 None 端点，升级后首连
  将返回明确配置错误，需补配 `SecurityPolicy=None`——这是 docs/07 要求的"None 仅显式配置"
  的预期行为变化，运维文档需写明。
- 层3 会话自愈/订阅不受影响：本次只改 `ConnectAsync` 建连前段（配置读取、端点选择、
  UserIdentity、证书生成/校验回调）与 `BuildConfiguration`，自愈（KeepAlive/
  SessionReconnectHandler/订阅核验）与轮询路径不动。
- 需要新增的落地点：凭据保护助手（宿主侧、AES-GCM + env 密钥）、`DomainMapper`/DTO 的
  Password 密文化与剔除、OPC UA 安全参数校验与端点筛选、证书管理 API + 前端表单/证书面板、
  安全参考服务器 scope 与对应测试。

## 载荷墙（硬约束）

- 不得改 `IProtocolDriver` / `ISubscriptionSource` 公共接口（只增不改）。
- 不得改 Modbus/S7 采集路径；其协议参数不受 OPC UA 安全键影响（按协议消费）。
- 禁止任何隐式降级 None；None 仅显式配置才允许。
- 禁止明文存密码（`appsettings.json`/SQLite/日志）；禁止把密钥落 DB/appsettings。
- 禁止 `e.Accept = true`；生产连接路径 `AutoAcceptUntrustedCertificates` 必须为 false。
- 证书信任状态以 pki 目录为唯一权威，不引入第二套落库副本（防双写漂移）。
- 证书管理/凭据为 OPC UA 层4 能力：不改既有 Web 登录/JWT/RBAC、Desktop DPAPI 行为；
  不把 OPC UA 做成对外 Server（北向仍 MQTT）。
- 层3 自愈/KeepAlive/订阅路径不改；本层安全改动只落在建连前段与 `BuildConfiguration`。
- 加解密助手只新增，不动既有 `PasswordHasher`（单向哈希）语义与调用方。
- Protocol 模块不引入加解密依赖；解密只发生在宿主组装 DeviceConnection 供连接之时。

## 变更记录

- 2026-09-02 创建，决策定稿（仅文档；实现与验收待执行，见 AC-opcua-layer4-security）。
  SDK 事实已复核：`CoreClientUtils.SelectEndpoint` 在 1.5.378.156 无可传 `SecurityPolicyUri`
  的过滤重载（更正 docs/07 表格括号注，同 ADR-072 更正纪律）；`UserIdentity(string, byte[])`
  与证书目录信任 API（`CertificateTrustList`/`ICertificateStore`）均存在。
  凭据落库现状已核实：`DomainMapper` 直存 Parameters JSON 明文 → 加密落库决策（D5）依据；
  Desktop `DpapiProtector` 为 Windows-only，不可用于 Linux Docker 生产。
