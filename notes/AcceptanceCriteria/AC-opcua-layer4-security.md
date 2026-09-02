# AC — OPC-UA 层次 4 安全与凭据（P0-2 + P2-1）

> 状态：**验收待执行**（ADR-073 文档定稿于 2026-09-02；本文为验收基线，实现与验收均未执行）。
> 执行时按 REMADE 逐条取证回填 PASS/FAIL，禁止只勾不取证。已知约束：安全集成用例使用
> 进程内安全参考服务器（加密端点 Basic256Sha256+SignAndEncrypt+UserName / None 两套），
> 不依赖 DataChange 通知，故不受层2/层3 已知"`CustomNodeManager2` 不产通知"测试服务器限制。

## 范围

为 OPC-UA 连接增加生产化安全：`DeviceConnection.Parameters` 支持安全策略/模式/用户名密码
（`SecurityPolicy`/`SecurityMode`/`UserName`/`Password`），按显式档位选端点且**无隐式 None
回退**；None 仅显式配置才允许；正确身份建会话、密码不落明文（加密落库 + 环境变量主密钥 +
前端不回显）；关闭 AutoAccept、非信任证书返回明确错误（BadCertificateUntrusted）；应用证书
失败不再静默降级；提供 rejected→trusted 证书管理 API 与前端入口并支持重试。

## ADR 引用

ADR-073（层4 连接安全与证书白名单，本验收范围）、ADR-072（层3 会话自愈，安全只动建连前段）、
ADR-019（驱动 `_gate` 串行、不产伪值、错误走 `OperationResult.Validation`）、ADR-071/070
（OPC UA 采集封装先例）；docs/07 层4 §5 W4/W5 约束卡与 §6 验证方案。

## 不在本次范围

层次 1 Browse（ADR-070）、层次 2 订阅（ADR-071）、层次 3 会话自愈（ADR-072）；
`IProtocolDriver`/`ISubscriptionSource` 公共接口；Modbus/S7 路径及其安全行为；证书身份认证
（`UserIdentity(CertificateIdentifier)`，P3 可选）；既有 Web 登录/JWT/RBAC 与 Desktop DPAPI；
不把 OPC UA 做成对外 Server。

## AC

- AC-1：策略/模式从 `Parameters` 读取并按显式档位选端点（P0-2a/b）。
  - 行为：运行 OPC UA 安全参数单测（需新增，如 `OpcUaDriverSecurityTests`，并入 V-2）：
    配置 `SecurityPolicy=Basic256Sha256`+`SecurityMode=SignAndEncrypt` 的加密端点 → 选中匹配
    加密端点并成功建连；目标仅匹配 None 端点且未显式声明 None → 返回配置错误而非连接成功。
    预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：`OpcUaDriver.cs` 从 `_connection.Parameters`
    读 `SecurityPolicy`/`SecurityMode`；建连前用 `GetEndpoints` 拉端点并按
    `EndpointDescription.SecurityPolicyUri`/`.SecurityMode` 手工过滤；无 SDK
    `SelectEndpoint` 策略重载调用；无"选端点失败后无条件回退 None"的 catch 分支。
- AC-2：None 仅显式配置才允许（P0-2a/W5/W4）。
  - 行为：安全参考服务器两套端点（None + Basic256Sha256/SignAndEncrypt）下：显式配置
    `SecurityPolicy=None` → 成功连 None 端点；不配置或配置加密档位 → 选加密端点；加密端点
    证书未信任时返回明确证书错误，**绝不静默落到 None**。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：选中 None 端点的唯一入口是
    `SecurityPolicy=None` 或 `SecurityMode=None` 显式声明；默认/错误路径均无隐式 None 回退。
- AC-3：用户名/密码身份（P0-2c）。
  - 行为：运行安全集成用例（并入 V-3）：正确用户名密码 → 成功建会话；错误密码 →
    明确认证错误（`BadUserAccessDenied`/`BadIdentityTokenRejected`，以参考服务器实际返回
    为准，非 500）；仅配置 `UserName` 而 `Password` 缺失/空 → `OperationResult.Validation`
    （400 而非 500）；两者均未配置 → 匿名建连（仅匿名端点）。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：有凭据时 `new UserIdentity(user,
    Encoding.UTF8.GetBytes(password))`，无凭据才 `new UserIdentity()`；`UserName` 有而
    `Password` 空 → 校验错误分支。
- AC-4：密码不落明文（凭据安全，层4 明示写 ADR 点）。
  - 行为：运行凭据持久化单测（需新增，如 `CredentialPersistenceTests`，并入 V-2）：保存含
    密码的 OPC UA 设备后，序列化/落库的 `ConnectionParams` JSON **不含明文 `Password`**（为
    密文或已剔除，且 `UserName` 保留）；`GET /api/devices/:id` 响应不含明文密码字段（仅
    `hasPassword` 标志）。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：`DomainMapper`/持久层写库前对 `Password` 加密或
    剔除；`DeviceDto` 无明文密码属性；`DeviceForm.vue` OPC UA 安全区密码框 `type=password`
    且编辑回填只给掩码/占位（留空=不改）；驱动/宿主不把明文密码写日志。
- AC-5：关闭 AutoAccept + 非信任证书明确拒绝（P2-1b）。
  - 行为：运行安全集成用例：未信任的加密端点服务器证书首连 → 连接失败、错误明确为
    证书校验失败（含 `BadCertificateUntrusted`，无静默连上）；该证书进入 rejected 目录。
    预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：`BuildConfiguration`
    `AutoAcceptUntrustedCertificates=false`；无 `CertificateValidation += (_, e) => e.Accept =
    true`；校验回调仅记录拒绝原因（Subject/Thumbprint/时间/错误码）。
- AC-6：应用证书失败不再静默（P2-1a）。
  - 行为：模拟 `opcua/pki/own` 目录不可写/生成失败 → 连接返回明确
    `SecurityConfigurationError`（含原因），**不再**静默降级 None 后"连接成功"。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：`CheckApplicationInstanceCertificates` 失败不再
    catch 后置 `_hasAppCertificate=false` 降级；类注释与 `BuildConfiguration` 注释不再保留
    "生产应改为…"的未落地表述。
- AC-7：证书管理 API 与信任流程（P2-1c）。
  - 行为：按 API 请求 + 期望状态码字段执行（V-3 关联或手工 curl）：首连被拒后
    `GET /api/opcua/certificates/rejected` 返回含该证书（Subject/Thumbprint/导入时间）；
    `POST /api/opcua/certificates/{thumbprint}/trust` → 200，该证书进入 trusted；触发重试连接
    → 成功；重复信任/未知指纹 → 4xx。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：证书管理服务操作 `opcua/pki/rejected` →
    `opcua/pki/trusted`（目录移动/SDK `ICertificateStore`）；信任状态不入 SQLite 设备表。
- AC-8：范围外未改动。
  - 验证：V-5 git 范围检查通过；`IProtocolDriver`/`ISubscriptionSource`、Modbus/S7、层3
    自愈（KeepAlive/重连/订阅核验）路径、既有 Web 登录/JWT/RBAC 与 Desktop DPAPI 无改动；
    无 OPC UA Server 能力新增。

## 验证命令

- V-1：`dotnet build NitroGateway.slnx`；预期 0 Error、0 阻塞。
- V-2：`dotnet test tests\NitroGateway.UnitTests --filter
  "FullyQualifiedName~OpcUaDriverSecurity|FullyQualifiedName~OpcUaParametersValidation|FullyQualifiedName~CredentialPersistence|FullyQualifiedName~CertificateTrustList"`；
  预期失败 0（含层4 新增用例：安全档位校验、None 仅显式、凭据加密落库断言）。
- V-3：`dotnet test tests\NitroGateway.IntegrationTests --filter
  "FullyQualifiedName~OpcUaDriverIntegrationTests"`；预期失败 0（安全 scope 用例：None/加密两套
  显式可达、首连拒绝→信任→成功、用户名密码对/错、无隐式回退；安全用例不依赖 DataChange）。
- V-4：`dotnet test tests\NitroGateway.UnitTests --no-restore`；预期失败 0（回归）。
- V-5：`git status --short` 与 `git diff --stat`；预期变更仅含 Protocol/OpcUa 建连安全段、
  Persistence/Webapi 凭据保护与证书管理 API、web 前端（DeviceForm 安全区 + 证书面板）、测试、
  notes（ADR-073/AC-layer4/worklog）与任务链文件，无范围外文件。

## 实测回填

（未执行——本文为验收基线。实现落地后按 REMADE 执行 V-1..V-5 并在此逐条回填
AC-1..AC-8 的 PASS/FAIL 与证据；届时禁止以"实现已写"替代行为取证。）
