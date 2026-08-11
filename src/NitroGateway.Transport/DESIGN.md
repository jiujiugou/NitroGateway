# Transport 层设计文档

## 定位

网络通信管道。封装 MQTT 和 HTTP 两种通道，对上暴露统一的消息收发接口。
不关心消息内容是什么，只负责"发出去"和"收进来"。

---

## 接口

### IMqttClient

```csharp
Task<OperationResult> ConnectAsync(CancellationToken ct);
Task<OperationResult> DisconnectAsync(CancellationToken ct);
Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos, CancellationToken ct);
Task<OperationResult> SubscribeAsync(string topic, int qos, CancellationToken ct);

IAsyncEnumerable<MqttMessage> Messages { get; }  // 接收到的消息流
MqttConnectionState State { get; }
event Action<MqttConnectionState>? StateChanged;
```

### MqttMessage

```csharp
string Topic { get; }
byte[] Payload { get; }
int Qos { get; }
DateTime ReceivedAt { get; }
```

### MqttConnectionState

```csharp
Disconnected / Connecting / Connected / Reconnecting / Faulted
```

### MqttConnectionOptions（ADR-020 P3-4 同步实际签名）

```csharp
string Host { get; }                    // broker 地址，必填（注册时校验非空）
int Port { get; }                       // 默认 1883
string? ClientId { get; }               // 留空自动生成 NitroGateway-{MachineName}-{8位随机}
string? Username { get; }
string? Password { get; }
bool UseTls { get; }
int KeepAliveSeconds { get; }           // 默认 60，夹紧 [5, 3600]
int MaxReconnectAttempts { get; }       // 默认 10，0 = 不自动重连，夹紧 ≥0
int ReconnectBackoffBaseMs { get; }     // 默认 1000，夹紧 ≥1
int ReconnectMaxIntervalMs { get; }     // 默认 30000，夹紧 ≥1
```

### IHttpClient（ADR-020 P3-4 同步实际签名）

```csharp
Task<OperationResult<HttpResponse>> SendAsync(HttpRequest request, CancellationToken ct);
Task<OperationResult> UploadAsync<T>(string path, T payload, CancellationToken ct);  // POST，非幂等不重试
Task<OperationResult> HealthCheckAsync(CancellationToken ct);                        // GET {HealthPath ?? "/health"}
HttpConnectionState State { get; }
event Action<HttpConnectionState>? StateChanged;
```

`HttpRequest` 和 `HttpResponse` 是简单的数据载体（URL、Method、Headers、Body、StatusCode），不依赖 ASP.NET Core。

---

## 调用时序

### Forwarder 通过 MQTT 上行数据

```
Forwarder                  IMqttClient                    MQTT Broker
    │                          │                              │
    │── ConnectAsync() ───────→│                              │
    │                          │── CONNECT ──────────────────→│
    │                          │←─ CONNACK ───────────────────│
    │←── Success ──────────────│                              │
    │                          │                              │
    │── PublishAsync(data) ───→│                              │
    │                          │── PUBLISH(qos=1) ───────────→│
    │                          │←─ PUBACK ────────────────────│
    │←── Success ──────────────│                              │
    │                          │                              │
    │── DisconnectAsync() ────→│                              │
    │                          │── DISCONNECT ───────────────→│
```

### 云端指令下行（通过 HTTP 接收）

```
Cloud API                   IHttpClient
    │                          │
    │── SendAsync(GET /cmd) ──→│
    │                          │── HTTP GET ────→ Cloud
    │                          │←─ 200 + JSON ───
    │←── HttpResponse ─────────│
```

### 重连机制

```
IMqttClient 内部自动处理，外部无感知：

Connected ──→ 断线 ──→ Reconnecting ──→ 退避重试 ──→ Connected
                          │                  │
                          └── 超过最大次数 ──→ Faulted ──→ 触发 StateChanged
```

---

## 约束

1. PublishAsync — 超时返回 `OperationalError.Timeout`，不抛异常
2. Messages — 用 `IAsyncEnumerable` 而非事件回调，支持 `CancellationToken` 和背压
3. 自动重连 — 驱动内部实现，最大重试次数可配，超过后状态变为 Faulted 并停止重试
4. IMqttClient — 基于 MQTTnet 库封装，不自己实现 MQTT 协议
5. IHttpClient — 基于 `HttpClient` / `SocketsHttpHandler` 封装，不引入额外的 HTTP 框架
6. QoS — 默认 QoS=1（至少一次）。ADR-020 P3-4：全仓发布点（Forwarder / MqttAlarmNotifier）均 QoS1，
   "配置数据同步用 QoS=2" 为预留约束（中心下发通道未落地，落地时再启用）
7. Payload — 统一 `byte[]`，不做序列化。序列化是 Forwarder 层的职责
8. ClientId — 若不指定，自动生成 `NitroGateway-{MachineName}-{Guid}`

---

## NuGet 依赖

| 子模块 | 包 |
|---|---|
| MQTT | `MQTTnet` |
| HTTP | 内置 `System.Net.Http`，无需额外包 |
