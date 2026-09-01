# OpcUa

OPC UA 协议驱动实现，支持订阅与读写。

层级 1 基础通信
| 能力项 | SDK 已提供（复用，不重写） | 你的现状 | 必须补的封装 |
| Endpoint | `CoreClientUtils.SelectEndpoint` + `ConfiguredEndpoint` | ✅ 已封装（含 None 回退） | 无 |
| Session | `Session.Create`（已用） | ✅ 已封装 | 无 |
| SecureChannel | SDK 自动（TransportChannel） | ✅ 自动 | 无 |
| Namespace | `NamespaceIndex`、`FetchNamespaceTablesAsync` | ✅ NodeId 四型解析 | 无 |
| Address Space / Node | Browse 服务 | ⚠️ 仅 Read 侧 | 见 Browse 项 |
| Read | `session.ReadAsync` | ✅ Read/ReadBatch | 无 |
| Write | `session.WriteAsync` | ✅ Write | 无 |
| **Browse** | `Session.Browse` + `BrowseDescription` | ❌ 只有接口存根 | **P0-①** 实现 `BrowseAsync` |
| DataType | `Variant`/`DataTypeIds` | ✅ 双向映射 | 无 |
| **NodeClass** | `NodeClass` 枚举 | ❌ 未读取 | 随 P0-① Browse 补 |
| **AccessLevel** | `AccessLevels` | ❌ 未读取 | 随 P0-① Browse 补 |

层次2实时采集
| 能力项 | SDK 已提供 | 你的现状 | 必须补的封装 |
| **Subscription** | `CreateSubscription(SubscriptionOptions)` + `Subscription.CreateAsync` | ❌ 未实现 | **P1-④** 订阅封装 |
| **MonitoredItem** | `CreateMonitoredItem`/`AddItem` + `CreateItemsAsync` | ❌ | 同上 |
| **SamplingInterval** | `MonitoredItem.SamplingInterval` | ❌ | 由 `DevicePoint.ScanIntervalMs` 映射 |
| **PublishingInterval** | `Subscription.PublishingInterval` | ❌ | 由设备/全局配置映射 |
| **DataChange** | `MonitoredItem.Notification` 事件 | ❌ | 事件 → 领域值 → Pipeline |
| StatusCode | `IsGood/IsBad/IsUncertain` | ⚠️ 只用了 IsBad | **P0-③** Uncertain 映射 |
| SourceTimestamp | `DataValue.SourceTimestamp` | ✅ 已读 | 无 |
| ServerTimestamp | `DataValue.ServerTimestamp` | ❌ 未用 | 可选：`RawPointValue` 加字段或忽略 |

层级3 工业可靠性
| 能力项 | SDK 已提供 | 你的现状 | 必须补的封装 |
| **KeepAlive** | `Session.KeepAlive` 事件 | ❌ 未接入 | **P1-⑤** 会话保活检测 |
| 连接检测 | 读 `Server_ServerStatus` | ✅ `PingAsync`/`ProbeLinkAsync` | 无 |
| **Session 恢复** | `SessionReconnectHandler.BeginReconnect` / `ReconnectAsync` | ⚠️ 仅 ReliableProtocolDriver 重建新 Session | **P1-⑤** 自动重连 |
| **Subscription 恢复** | `TransferSubscriptionsAsync` / `RecreateSubscriptionsAsync` | ❌ | 随订阅封装补 |
| **MonitoredItem 恢复** | 随订阅重建 | ❌ | 同上 |
| Timeout | `TransportQuotas.OperationTimeout` | ✅ 与 RequestTimeoutMs 对齐 | 无 |
| Retry | `ReliableProtocolDriver` + Polly | ✅ | 无 |
| **Reconnect** | `SessionReconnectHandler` | ⚠️ 每次重建会话 | **P1-⑤** |

层级4安全
| 能力项 | SDK 已提供 | 你的现状 | 必须补的封装 |
| Application Certificate | `ApplicationInstance.CheckApplicationInstanceCertificates` | ⚠️ 尽力而为、失败静默降级 | **P2-⑥** 证书不可用流程 |
| Trust List | `CertificateTrustList`（目录已配） | ⚠️ `AutoAcceptUntrustedCertificates=true` | **P2-⑥** 去 AutoAccept、白名单校验 |
| SecurityPolicy | `SecurityPolicies` + `SelectEndpoint(useSecurity)` | ⚠️ 自动选、硬回退 None | **P0-②** 可配置策略 |
| SecurityMode | `MessageSecurityMode` | ⚠️ 同上 | **P0-②** |
| Anonymous | `new UserIdentity()` | ✅ 默认 | 无 |
| **Username/Password** | `new UserIdentity(user, pass)` / `UserNameIdentityToken` | ❌ 硬编码匿名 | **P0-②** 从 Parameters 读取凭据 |
| **Certificate Auth** | `CertificateIdentityToken` | ❌ | 进阶（可选） |
| Sign / SignAndEncrypt | SDK 随证书+策略自动处理 | ⚠️ 依赖证书互认 | 不需要手写，依赖 P0-②/P2-⑥ |