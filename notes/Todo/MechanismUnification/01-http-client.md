# 01 · HTTP 访问统一

- 类别：完全统一（差异是参数）
- 优先级：高
- 状态：待办

## 现状（三套 HTTP 客户端）

| 位置 | 文件 | 实现 |
|---|---|---|
| 传输层 | `src/NitroGateway.Transport/HTTP/HttpClientWrapper.cs:51` | `new HttpClient(handler)`，未用 `IHttpClientFactory` |
| Webapi | `src/NitroGateway.Webapi/Services/CenterConfigClient.cs:51` | 独立 `new HttpClient`（注释明确不复用 `Transport.IHttpClient`） |
| Desktop | `src/NitroGateway.Desktop/Services/Sync/CenterConfigClient.cs:50` | 同上 |

同一 HTTP 能力两个接口/三处实现，连接池与韧性策略无法统一。

## 统一形态

- 用 `IHttpClientFactory` 注册**具名 client**（`Transport.IHttpClient` 用的一个，Center 配置客户端一个）。
- 韧性（超时/重试）由 `DelegatingHandler`（见 [02](02-resilience-pipeline.md)）挂上，而不是每个类自建。
- 认证/BaseUrl/超时 → 具名 client 配置（**参数**）。

## 差异是参数还是语义

参数：BaseUrl、认证方式、超时、重试策略。无语义差异。

## 涉及改动文件（待办）

- [ ] `src/NitroGateway.Transport/HTTP/HttpClientWrapper.cs`：接受 `HttpClient`/`IHttpClientFactory` 注入，去掉自建
- [ ] `src/NitroGateway.Transport/HTTP/TransportServiceCollectionExtensions.cs`（或注册处）：`AddHttpClient` 注册
- [ ] `src/NitroGateway.Webapi/Services/CenterConfigClient.cs`：注入工厂
- [ ] `src/NitroGateway.Desktop/Services/Sync/CenterConfigClient.cs`：注入工厂

## 验收 / 测试

- 现有 `HttpClientWrapperTests`、`CenterConfigClientTests`（若存在）保持绿。
- 新增：同一 named client 被复用（handler 不重复创建）。

## 风险

- 具名 client 的 `BaseAddress` 是 per-client 的；不同 BaseUrl 需不同 named client。
- Desktop 的 DI 注册需确认注入路径。
