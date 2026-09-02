using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

// OPC Foundation SDK 1.5 将经典同步 API（Validate / SelectEndpoint / Session.Create /
// CheckApplicationInstanceCertificates / CloseSession / CertificateValidator()）标记为 Obsolete，
// 但 1.5.378.156 仍稳定可用且为 SDK 当前主推兼容路径（ASYNC 版返回 ISession 并需 ITelemetryContext，
// 依赖注入代价高、无额外收益）。此处统一压制 CS0618，避免噪音；升级 SDK 大版本时再迁移。
#pragma warning disable CS0618

namespace NitroGateway.Protocols.OpcUa;

/// <summary>
/// OPC UA 协议驱动（采集侧 Client），基于 OPC Foundation .NET Standard SDK 1.5.378.156。
/// 生命周期：<c>ConnectAsync</c>（选端点 + 建 Session）→ Read/Write → <c>DisconnectAsync</c>。
/// 同时支持轮询与 Subscription；订阅通知仅作为原始值来源，仍复用 Collection 既有管道。Browse
/// （<see cref="IBrowseableDriver"/>，ADR-070）已实现，供配置工具/前端选点，采集引擎不调。
/// </summary>
/// <remarks>
/// <para><b>并发闸门（ADR-019 P2-1）：</b>OPC UA Session 非线程安全，全部通信（读/写/连接/断开/Ping）
/// 经 <see cref="_gate"/> 串行化，防止 1s 采集读 + Webapi 写 + 健康 Ping 并发访问同一 Session 导致
/// 请求交错/协议失步（与 Modbus/S7 驱动同一约束）。</para>
/// <para><b>失败读不产伪值（ADR-019 P1-1）：</b>Read 响应显式检查 <c>StatusCode</c>，
/// Bad/Uncertain 状态跳过该点位（SDK 在 Bad 时 <c>WrappedValue</c> 为默认值，直接取会把故障读当作
/// 0.0 + Good 写入时序库并上云）；全部失败复位 <see cref="DriverState.Faulted"/>，让上层重试管线重新建连。</para>
/// <para><b>连接安全（ADR-073 层4）：</b>安全档位（<c>SecurityPolicy</c>/<c>SecurityMode</c>/
/// <c>UserName</c>/<c>Password</c>）由 <see cref="DeviceConnection.Parameters"/> 显式声明，None 仅
/// 显式配置才允许；建连前 GetEndpoints 手工按策略/模式选端点，无隐式 None 回退。应用证书在
/// <c>opcua/pki/own</c> 生成，失败显式返回 <see cref="OperationalError"/> 而非静默降级。服务端证书按
/// <c>opcua/pki/trusted</c> 白名单校验（<c>AutoAcceptUntrustedCertificates=false</c>，无 Accept 回调）；
/// 未信任证书由 SDK 判 <c>BadCertificateUntrusted</c> 拒绝并进入 <c>opcua/pki/rejected</c>，经证书管理
/// API 信任后重试（D8）。</para>
/// </remarks>
public sealed class OpcUaDriver : IProtocolDriver, IBrowseableDriver, ISubscriptionSource, IDisposable
{
    /// <summary>应用证书 SubjectName；首次连接自动生成到 opcua/pki/own 目录存储</summary>
    private const string AppSubjectName = "CN=NitroGateway, DC=localhost";

    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private readonly OpcUaAddressParser _addressParser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前会话；null 表示未连接。会话非线程安全，全部通信经 <see cref="_gate"/> 串行化</summary>
    private Session? _session;
    private Subscription? _subscription;
    private string? _subscriptionSignature;

    /// <summary>会话自愈（ADR-072）：当前活动的重连 handler；null 表示无进行中自愈。</summary>
    private SessionReconnectHandler? _reconnectHandler;
    /// <summary>已绑定 <c>KeepAlive</c> 事件的会话；用于幂等解绑（ADR-072 D1/D6）。</summary>
    private Session? _keepAliveSession;
    /// <summary>自愈防重入位（0/1，经 Interlocked 访问）：1 表示已有活动重连（ADR-072 D3）。</summary>
    private int _reconnectActive;

    /// <inheritdoc />
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc />
    public DriverCapability Capability => OpcUaDriverCapability.Instance;

    /// <inheritdoc />
    public event Func<IReadOnlyList<RawPointValue>, Task>? ValuesReceived;

    /// <inheritdoc />
    public bool IsSubscriptionActive => _subscription is not null;

    /// <summary>创建 OPC UA 驱动。由 <see cref="OpcUaRegistration"/> 注册到复合工厂（ILogger 非泛型，匹配工厂 CreateLogger(protocol.Name)）</summary>
    public OpcUaDriver(DeviceConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // 二次检查：等待闸门期间可能已被其他连接请求完成
            if (State == DriverState.Connected && _session is not null)
                return OperationResult.Success();

            if (string.IsNullOrWhiteSpace(_connection.Endpoint))
            {
                State = DriverState.Faulted;
                return OperationalError.Validation("OPC UA 端点（opc.tcp://host:port）不能为空");
            }

            // ADR-073 D1：安全参数契约与校验（空值/类型错误/非法枚举/冲突组合 → Validation 400，绝不 500）
            var security = OpcUaSecurityParameters.Parse(_connection.Parameters);
            if (!security.IsValid)
            {
                State = DriverState.Faulted;
                return OperationalError.Validation(
                    $"OPC UA 安全参数配置错误: {string.Join("；", security.Errors)}");
            }
            var requirement = security.Requirement!;

            State = DriverState.Connecting;
            ct.ThrowIfCancellationRequested();

            try
            {
                // ADR-019 P2-4：操作超时与设备请求超时对齐（取 RequestTimeoutMs，下限 1s）
                var requestTimeout = Math.Max(1000, _connection.RequestTimeoutMs);

                // 1) 程序化构建 ApplicationConfiguration（不依赖 XML 配置文件，SDK 1.5 支持直接构造）
                var config = BuildConfiguration(requestTimeout);
                await config.Validate(ApplicationType.Client);
                // ADR-073 D6：不挂任何 CertificateValidation 订阅，避免 SDK 事件语义覆盖信任库校验；
                // AutoAcceptUntrustedCertificates=false（见 BuildConfiguration）。服务端证书按
                // opcua/pki/trusted 白名单校验，未信任证书由 SDK 判 BadCertificateUntrusted 并写入
                // opcua/pki/rejected，前端可经证书管理 API “信任→重试”。

                // 2) 应用证书（ADR-073 D7）：失败不再静默降级 None，显式返回 SecurityConfigurationError
                try
                {
                    var app = new ApplicationInstance
                    {
                        ApplicationName = "NitroGateway",
                        ApplicationType = ApplicationType.Client,
                        ApplicationConfiguration = config
                    };
                    var ok = await app.CheckApplicationInstanceCertificates(silent: true);
                    if (!ok)
                    {
                        State = DriverState.Faulted;
                        return OperationalError.SecurityConfiguration(
                            "OPC UA 应用证书初始化失败：无法生成或加载应用证书（opcua/pki/own），" +
                            "请检查该目录是否可写后重试。");
                    }
                }
                catch (Exception ex)
                {
                    State = DriverState.Faulted;
                    return OperationalError.SecurityConfiguration(
                        $"OPC UA 应用证书初始化失败: {ex.Message}（opcua/pki/own 目录不可写或证书生成失败）");
                }

                // 3) 选端点（ADR-073 D2/D3）：GetEndpoints 拉端点后按策略/模式手工过滤
                // （SDK 无策略过滤 SelectEndpoint 重载，见 ADR-073 Context 更正）；无隐式 None 回退
                var (selected, selectionError) = await DiscoverAndSelectEndpointAsync(config, requirement, ct);
                if (selected is null)
                {
                    State = DriverState.Faulted;
                    return OperationalError.Validation(selectionError ?? "OPC UA 无可用的匹配端点");
                }

                // 4) 建会话（身份按 ADR-073 D4；updateBeforeConnect=false 不重复发现）
                var configuredEndpoint = new ConfiguredEndpoint(selected.Server, EndpointConfiguration.Create(config));
                configuredEndpoint.Update(selected);
                var session = await Session.Create(
                    config,
                    configuredEndpoint,
                    updateBeforeConnect: false,
                    checkDomain: false,
                    "NitroGateway",
                    (uint)Math.Max(5000, requestTimeout),
                    BuildUserIdentity(requirement),
                    null,
                    ct);

                ct.ThrowIfCancellationRequested();
                _session = session;
                State = DriverState.Connected;
                // ADR-072 D1：连接成功即绑定 KeepAlive，作为"已连接后断线"的自愈检测入口
                BindKeepAlive(session);
                _logger.LogInformation("OPC UA 已连接: {Endpoint} 安全={SecurityMode}/{SecurityPolicy}",
                    _connection.Endpoint,
                    selected.SecurityMode,
                    string.IsNullOrEmpty(selected.SecurityPolicyUri) ? "None" : selected.SecurityPolicyUri);
                return OperationResult.Success();
            }
            catch (OperationCanceledException)
            {
                State = DriverState.Faulted;
                return OperationalError.Timeout("OPC UA 连接已取消");
            }
            catch (ServiceResultException ex)
            {
                // ADR-073：SDK 服务级拒绝（证书未信任 BadCertificateUntrusted / 认证拒绝
                // BadUserAccessDenied / BadIdentityTokenRejected 等）映射为 Communication，
                // 消息内含 SDK 状态码供前端区分；不吞成 Timeout。绝不静默落到 None。
                State = DriverState.Faulted;
                return OperationalError.Communication($"OPC UA 连接被拒绝: {ex.Message}");
            }
            catch (Exception ex)
            {
                State = DriverState.Faulted;
                return OperationalError.Timeout($"OPC UA 连接失败: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // ADR-072 D6：先停自愈重连 handler、解绑 KeepAlive，再关会话
            // （顺序不可反，防止重连回调/保活事件访问已关闭会话）
            CancelReconnectHandler();
            UnbindKeepAlive();
            await DeleteSubscriptionAsync(ct);
            var session = _session;
            _session = null;
            if (session is not null)
            {
                // CloseSession + Dispose：SDK 1.5 无 Session.Close，用 CloseSession 发关闭请求再释放
                try { session.CloseSession(null, true); } catch { }
                try { session.Dispose(); } catch { }
            }
            State = DriverState.Disconnected;
            return OperationResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnsureSubscriptionAsync(
        IReadOnlyList<DevicePoint> points,
        int publishingIntervalMs,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is null || State != DriverState.Connected)
                return OperationalError.Unavailable("OPC UA 未连接，无法创建订阅");
            if (points.Count == 0)
                return OperationalError.Validation("OPC UA 订阅至少需要一个启用点位");

            var interval = Math.Max(1, publishingIntervalMs);
            var signature = BuildSubscriptionSignature(points, interval);
            if (_subscription is not null && _subscriptionSignature == signature)
                return OperationResult.Success();

            await DeleteSubscriptionAsync(ct);
            try
            {
                // 预解析所有点位的地址，非法地址直接失败，避免产生半成品订阅泄漏到 Session
                var parsed = new List<(DevicePoint Point, OpcUaAddress Address)>(points.Count);
                foreach (var point in points)
                {
                    if (_addressParser.Parse(point.Address) is not OpcUaAddress address)
                        return OperationalError.Validation($"OPC UA 订阅点位地址格式不合法: {point.Address}");
                    parsed.Add((point, address));
                }

                // SDK 1.5.378 起 Session.CreateSubscription 被移除：改用
                // new Subscription(TelemetryContext, options) + Session.AddSubscription + CreateAsync；
                // SessionFactory 由 Session 构造函数始终初始化，Telemetry 恒非空（MonitoredItem 要求非空）。
                var telemetry = _session.SessionFactory.Telemetry;
                var subscription = new Subscription(telemetry, new SubscriptionOptions
                {
                    DisplayName = "NitroGateway.Collection",
                    PublishingInterval = interval,
                    KeepAliveCount = 10,
                    LifetimeCount = 30,
                    MaxNotificationsPerPublish = 0
                });
                _session.AddSubscription(subscription);

                foreach (var (point, address) in parsed)
                {
                    var item = new MonitoredItem(telemetry, new MonitoredItemOptions
                    {
                        DisplayName = point.Name,
                        StartNodeId = ToNodeId(address),
                        AttributeId = Attributes.Value,
                        SamplingInterval = point.ScanIntervalMs > 0 ? point.ScanIntervalMs : interval,
                        QueueSize = 1,
                        DiscardOldest = true
                    });
                    item.Handle = point;
                    item.Notification += OnMonitoredItemNotification;
                    subscription.AddItem(item);
                }

                _subscription = subscription;
                await subscription.CreateAsync(ct);
                _subscriptionSignature = signature;
                _logger.LogInformation("OPC UA 订阅已创建：{PointCount} 点，发布间隔 {IntervalMs}ms",
                    points.Count, interval);
                return OperationResult.Success();
            }
            catch (OperationCanceledException)
            {
                await DeleteSubscriptionAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await DeleteSubscriptionAsync(CancellationToken.None);
                return OperationalError.Protocol($"OPC UA 创建订阅失败: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await DeleteSubscriptionAsync(ct);
            return OperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationalError.Protocol($"OPC UA 停止订阅失败: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is null || State != DriverState.Connected)
                return OperationalError.Unavailable("OPC UA 未连接");

            try
            {
                // 最小代价读 ServerStatus 节点验证链路连通
                var nodes = new ReadValueIdCollection
                {
                    new() { NodeId = VariableIds.Server_ServerStatus, AttributeId = Attributes.Value }
                };
                var response = await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, nodes, ct);
                if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
                    return OperationResult.Success();
                return OperationalError.Timeout("OPC UA 连接验证失败：ServerStatus 不可读");
            }
            catch (Exception ex)
            {
                return OperationalError.Timeout($"Ping 失败: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        var result = await ReadBatchAsync([point], ct);
        if (result.IsFailure) return result.Error!;
        var first = result.Value!.FirstOrDefault();
        return first is not null
            ? OperationResult<RawPointValue>.Success(first)
            : OperationalError.Protocol($"读取失败: {point.Name}");
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is null || State != DriverState.Connected)
                return OperationalError.Unavailable("OPC UA 未连接");

            var pointList = points.ToList();
            if (pointList.Count == 0)
            {
                // ADR-031：空点位设备也要发一次真实探测读验证链路，否则断开后仍 Connected 且无流量 → 假在线
                return await ProbeLinkAsync(ct);
            }

            // 地址解析：非法地址跳过该点位（记 Warning），其余合并为一次批量读
            var nodesToRead = new ReadValueIdCollection();
            var validPoints = new List<DevicePoint>(pointList.Count);
            foreach (var p in pointList)
            {
                try
                {
                    var uaAddr = (OpcUaAddress)_addressParser.Parse(p.Address);
                    nodesToRead.Add(new ReadValueId { NodeId = ToNodeId(uaAddr), AttributeId = Attributes.Value });
                    validPoints.Add(p);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("点位 {Name} 地址非法已跳过: {Address}（{Error}）", p.Name, p.Address, ex.Message);
                }
            }

            // 全部地址非法属配置错误，非通信故障：返回 Protocol 但不复位 Faulted
            if (validPoints.Count == 0)
                return OperationalError.Protocol($"批量读取失败：{pointList.Count} 个点位地址均非法");

            var response = await _session.ReadAsync(null, 0, TimestampsToReturn.Both, nodesToRead, ct);

            var results = new List<RawPointValue>();
            for (var i = 0; i < validPoints.Count && i < response.Results.Count; i++)
            {
                var dv = response.Results[i];
                // ADR-019 P1-1：Bad/Uncertain 不产伪值（SDK Bad 时 WrappedValue 为默认值）
                if (StatusCode.IsBad(dv.StatusCode))
                {
                    _logger.LogWarning("点位 {Name} 读取 Bad: {Code}", validPoints[i].Name, dv.StatusCode);
                    continue;
                }
                results.Add(new RawPointValue
                {
                    Point = validPoints[i],
                    Value = VariantToValue(dv.WrappedValue),
                    // 源时间戳缺失时用本地采集时间兜底
                    Timestamp = dv.SourceTimestamp == DateTime.MinValue ? DateTime.UtcNow : dv.SourceTimestamp
                });
            }

            // ADR-019 P3-1 + ADR-072 D5：全部失败复位 Faulted，让重试管线重新建连
            // （与 Modbus/S7 对齐）；自愈重连窗口内不置 Faulted（保持 Connected，防与上层抢道）
            if (results.Count == 0)
            {
                EnterFaultedIfNotSelfHealing();
                return OperationalError.Protocol($"批量读取失败：{validPoints.Count} 个点位均未返回数据");
            }

            if (results.Count < validPoints.Count)
                _logger.LogWarning("批量读取部分失败：{Ok}/{Total} 个点位成功", results.Count, validPoints.Count);

            return results;
        }
        catch (Exception ex)
        {
            EnterFaultedIfNotSelfHealing();
            return OperationalError.Protocol($"OPC UA 读取失败: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is null || State != DriverState.Connected)
                return OperationalError.Unavailable("OPC UA 未连接");

            try
            {
                var uaAddr = (OpcUaAddress)_addressParser.Parse(point.Address);
                var nodesToWrite = new WriteValueCollection
                {
                    new()
                    {
                        NodeId = ToNodeId(uaAddr),
                        AttributeId = Attributes.Value,
                        // ADR-019：按点位声明类型构造 Variant。Webapi 写入值通常来自 JSON（数值一律为 double），
                        // 若直接按 .NET 类型映射，Float 点会发成 Double → 服务端 BadTypeMismatch（实测）。
                        Value = new DataValue(ToVariant(point.DataType, value))
                    }
                };
                var response = await _session.WriteAsync(null, nodesToWrite, ct);
                if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0]))
                    return OperationResult.Success();
                var code = response.Results.Count > 0 ? response.Results[0].ToString() : "无响应";
                return OperationalError.Protocol($"写入失败: {code}");
            }
            catch (Exception ex)
            {
                return OperationalError.Protocol($"写入失败: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries)
        {
            var r = await WriteAsync(p, v, ct);
            if (r.IsFailure) return r;
        }
        return OperationResult.Success();
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
        string parentNodeId = "", CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is null || State != DriverState.Connected)
                return OperationalError.Unavailable("OPC UA 未连接");

            // ADR-070：parent 缺省 = Objects 目录（i=85）；否则复用现有解析器转 NodeId。
            // 非法父地址走 OperationResult 错误（配置工具输入错误，非通信故障）。
            NodeId parentNode;
            try
            {
                parentNode = string.IsNullOrWhiteSpace(parentNodeId)
                    ? ObjectIds.ObjectsFolder
                    : ToNodeId((OpcUaAddress)_addressParser.Parse(parentNodeId));
            }
            catch (Exception ex)
            {
                return OperationalError.Validation($"非法 OPC UA 父节点地址: {ex.Message}");
            }

            // 只取层级引用（含子类型），NodeClass 限定 Object|Variable（属性/方法/类型节点滤掉），
            // 结果属性覆盖 DisplayName / NodeClass / TypeDefinition。
            var nodesToBrowse = new BrowseDescriptionCollection
            {
                new BrowseDescription
                {
                    NodeId = parentNode,
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                    IncludeSubtypes = true,
                    NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                    ResultMask = (uint)(BrowseResultMask.DisplayName | BrowseResultMask.NodeClass | BrowseResultMask.TypeDefinition)
                }
            };

            // 一次 Browse + 循环 BrowseNext 展开分页（ContinuationPoint），直至无续页
            var references = new List<ReferenceDescription>();
            var first = await _session.BrowseAsync(null, null, 0, nodesToBrowse, ct);
            if (first.Results is null || first.Results.Count == 0)
                return OperationalError.Protocol("OPC UA 浏览无响应");
            // 父节点不存在/无权限时服务器在 BrowseResult.StatusCode 返回 Bad（如 BadNodeIdUnknown）：
            // 地址语法已通过解析，属服务端数据问题 → 返回 Protocol 错误（ADR-070，不置 Faulted）。
            var firstResult = first.Results[0];
            if (StatusCode.IsBad(firstResult.StatusCode))
                return OperationalError.Protocol($"OPC UA 浏览失败: {firstResult.StatusCode}");
            CollectReferences(firstResult, references);

            var continuation = firstResult.ContinuationPoint;
            while (continuation is { Length: > 0 })
            {
                var next = await _session.BrowseNextAsync(
                    null, false, new ByteStringCollection { continuation }, ct);
                if (next.Results is null || next.Results.Count == 0)
                    return OperationalError.Protocol("OPC UA 浏览分页失败");
                var nextResult = next.Results[0];
                if (StatusCode.IsBad(nextResult.StatusCode))
                    return OperationalError.Protocol($"OPC UA 浏览分页失败: {nextResult.StatusCode}");
                CollectReferences(nextResult, references);
                continuation = nextResult.ContinuationPoint;
            }

            // 变量节点批量补读 DataType + AccessLevel → 映射 TypeName / Access（一次 Read 请求）。
            // 用与 variableNodes 同序的平行数组存映射，避免 ExpandedNodeId / NodeId 字典键类型混用。
            var variableNodes = references.Where(r => r.NodeClass == NodeClass.Variable).ToList();
            var typeNames = new string[variableNodes.Count];
            var accesses = new string[variableNodes.Count];
            if (variableNodes.Count > 0)
            {
                var readIds = new ReadValueIdCollection();
                foreach (var v in variableNodes)
                {
                    readIds.Add(new ReadValueId { NodeId = (NodeId)v.NodeId, AttributeId = Attributes.DataType });
                    readIds.Add(new ReadValueId { NodeId = (NodeId)v.NodeId, AttributeId = Attributes.AccessLevel });
                }
                var attrResults = await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, readIds, ct);
                for (var i = 0; i < variableNodes.Count && (2 * i + 1) < attrResults.Results.Count; i++)
                {
                    var typeDv = attrResults.Results[2 * i];
                    var accessDv = attrResults.Results[2 * i + 1];
                    if (StatusCode.IsGood(typeDv.StatusCode) && typeDv.Value is NodeId typeId)
                        typeNames[i] = DataTypeName(typeId);
                    if (StatusCode.IsGood(accessDv.StatusCode) && accessDv.Value is byte access)
                        accesses[i] = AccessToString(access);
                }
            }

            // ReferenceDescription → BrowseNode（NodeId 用 AddressParser 同格式序列化，可直接回填点位）
            var results = new List<BrowseNode>(references.Count);
            var varIndex = 0;
            for (var i = 0; i < references.Count; i++)
            {
                var r = references[i];
                var isVariable = r.NodeClass == NodeClass.Variable;
                results.Add(new BrowseNode
                {
                    NodeId = SerializeNodeId(r.NodeId),
                    Name = string.IsNullOrEmpty(r.DisplayName.Text) ? r.BrowseName.Name : r.DisplayName.Text,
                    TypeName = isVariable ? (varIndex < typeNames.Length ? (typeNames[varIndex] ?? "Unknown") : "Unknown") : "",
                    IsVariable = isVariable,
                    Access = isVariable ? (varIndex < accesses.Length ? accesses[varIndex] ?? "" : "") : ""
                });
                if (isVariable) varIndex++;
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ADR-070：浏览是只读配置工具，失败/超时不置 Faulted（不污染采集状态机），只返回错误
            return OperationalError.Protocol($"OPC UA 浏览失败: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // ADR-072 D6：先停自愈重连 handler、解绑 KeepAlive，再关会话（幂等，单条各自吞异常）
        try { CancelReconnectHandler(); } catch { }
        try { UnbindKeepAlive(); } catch { }
        try { DeleteSubscriptionAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { _session?.CloseSession(null, true); } catch { }
        _session?.Dispose();
        _gate.Dispose();
    }

    private async Task DeleteSubscriptionAsync(CancellationToken ct)
    {
        var subscription = _subscription;
        _subscription = null;
        _subscriptionSignature = null;
        if (subscription is null)
            return;

        foreach (var item in subscription.MonitoredItems)
            item.Notification -= OnMonitoredItemNotification;
        try { await subscription.DeleteAsync(true, ct); } catch { }
        subscription.Dispose();
    }

    // ── ADR-072：会话自愈（KeepAlive → SessionReconnectHandler → 订阅核验 → 生命周期清理）──

    /// <summary>
    /// 把 <c>KeepAlive</c> 事件绑定到指定会话（ADR-072 D1）。幂等：同一会话重复绑定跳过；
    /// 换会话时先解绑旧绑定再绑新会话，避免重复委托。须在 <c>_gate</c> 内调用。
    /// </summary>
    private void BindKeepAlive(Session session)
    {
        if (ReferenceEquals(_keepAliveSession, session))
            return;
        UnbindKeepAlive();
        session.KeepAlive += OnSessionKeepAlive;
        _keepAliveSession = session;
    }

    /// <summary>解绑 <c>KeepAlive</c> 事件（ADR-072 D1/D6）。幂等：未绑定/已解绑时无动作。</summary>
    private void UnbindKeepAlive()
    {
        var bound = _keepAliveSession;
        _keepAliveSession = null;
        if (bound is not null)
            bound.KeepAlive -= OnSessionKeepAlive;
    }

    /// <summary>
    /// 停止活动自愈重连并清理（ADR-072 D6）。幂等、可从任意线程调用；
    /// 调用后即便重连回调迟到，也会因会话已置空/关闭而直接返回。
    /// </summary>
    private void CancelReconnectHandler()
    {
        Interlocked.Exchange(ref _reconnectActive, 0);
        var handler = Interlocked.Exchange(ref _reconnectHandler, null);
        if (handler is null)
            return;
        try { handler.CancelReconnect(); } catch { }
        try { handler.Dispose(); } catch { }
    }

    /// <summary>
    /// <c>Session.KeepAlive</c> 事件处理（ADR-072 D1）。运行在 SDK 保活线程：
    /// 只做事件分类，不手写恢复路径；仅在确认"当前会话 + Connected + 无进行中重连"后，
    /// 用有界等待取得 <c>_gate</c> 复核并启动 <see cref="SessionReconnectHandler"/>。
    /// </summary>
    private void OnSessionKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        // Good/空状态 = 会话存活，无动作（D1）
        if (e.Status is null || StatusCode.IsGood(e.Status.Code))
            return;

        // 已有活动自愈重连：忽略重复 Bad 触发（防重入，D3）——快速路径，不争闸门
        if (Volatile.Read(ref _reconnectActive) != 0)
        {
            _logger.LogDebug("OPC UA 保活中断（{Code}）但已有自愈重连进行中，忽略", e.Status.Code);
            return;
        }

        // 与 Disconnect/Dispose 串行（D6）：有界等待 _gate，闸门内复核后再启动自愈。
        // 等不到（驱动正断开/闸门被长时间占用）则跳过，由下一次保活或上层重试管线兜底。
        bool acquired;
        try { acquired = _gate.Wait(TimeSpan.FromSeconds(2)); }
        catch { acquired = false; }
        if (!acquired)
        {
            _logger.LogDebug("OPC UA 保活中断（{Code}）但无法取得闸门，暂不启动自愈", e.Status.Code);
            return;
        }
        try
        {
            // 闸门内复核：会话已被置空/替换、状态已迁移、事件来自旧会话 → 放弃（D6 幂等）
            if (!ShouldStartSelfHeal(e.Status, session, _session, State, Volatile.Read(ref _reconnectActive)))
            {
                _logger.LogDebug("OPC UA 保活中断但不触发自愈（非当前会话/未连接/已有重连）: {Code}",
                    e.Status.Code);
                return;
            }

            var current = _session;
            if (current is null)
                return;
            // 闸门内无并发启动，直接置位（防重入位，D3）
            Interlocked.Exchange(ref _reconnectActive, 1);
            _logger.LogWarning("OPC UA 保活中断（{Code}），启动会话自愈重连（当前状态 {State}）",
                e.Status.Code, e.CurrentState);
            var telemetry = current.SessionFactory.Telemetry;
            var handler = new SessionReconnectHandler(telemetry);
            _reconnectHandler = handler;
            // 第二参数为毫秒重连周期（SDK 1.5.378.156 语义，非重试次数；ADR-072 已更正 docs/07）
            handler.BeginReconnect(current, SessionReconnectHandler.DefaultReconnectPeriod, OnReconnectComplete);
        }
        catch (Exception ex)
        {
            var pending = Interlocked.Exchange(ref _reconnectHandler, null);
            try { pending?.Dispose(); } catch { }
            Interlocked.Exchange(ref _reconnectActive, 0);
            _logger.LogError(ex, "启动 OPC UA 会话自愈重连失败");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// ADR-072 D1 保活事件分类（纯判定，便于无 SDK 会话的单测）：是否应启动会话自愈。
    /// false 情形：Good/空状态（存活）；已有重连进行中（防重入，D3）；事件非当前会话
    /// （旧会话迟到事件，D6）；驱动未处于 <see cref="DriverState.Connected"/>（自愈只接管
    /// "已连接后的断线"，D2）。
    /// </summary>
    internal static bool ShouldStartSelfHeal(
        ServiceResult? status,
        object? session,
        object? currentSession,
        DriverState state,
        int reconnectActive)
    {
        if (status is null || ServiceResult.IsGood(status))
            return false;
        if (reconnectActive != 0)
            return false;
        if (session is null || currentSession is null || !ReferenceEquals(session, currentSession))
            return false;
        if (state != DriverState.Connected)
            return false;
        return true;
    }

    /// <summary>SDK 重连完成回调（原地保住或重建成功各回调一次；SDK 定时器/线程池线程）。
    /// 不在回调线程内同步持 <c>_gate</c>，转发到后台异步处理（ADR-072 D6）。</summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        if (sender is not SessionReconnectHandler handler)
        {
            // 无法定位恢复结果：清防重入位兜底，交由既有恢复路径
            Interlocked.Exchange(ref _reconnectActive, 0);
            return;
        }
        _ = HandleReconnectCompleteAsync(handler);
    }

    /// <summary>后台处理重连完成结果：状态对齐、会话引用替换、订阅核验（ADR-072 D3/D4/D5）。</summary>
    private async Task HandleReconnectCompleteAsync(SessionReconnectHandler handler)
    {
        try
        {
            // 回调已触发即表示 handler 完成，驱动不再持有其引用
            Interlocked.CompareExchange(ref _reconnectHandler, null, handler);
            var replacement = handler.Session;

            if (replacement is null)
            {
                // 自愈被取消/无可用会话：清防重入位，交给既有重试管线（D7 兜底）
                Interlocked.Exchange(ref _reconnectActive, 0);
                _logger.LogWarning("OPC UA 会话自愈未获可用会话（可能已被取消），交由上层重试管线");
                return;
            }

            // 原地重连成功：会话实例未换，订阅原样保留（D3），仅记日志并清防重入位
            if (ReferenceEquals(replacement, Volatile.Read(ref _session)))
            {
                Interlocked.Exchange(ref _reconnectActive, 0);
                _logger.LogInformation("OPC UA 会话自愈成功（原地重连，会话与订阅保留）");
                return;
            }

            if (replacement is not Session newSession)
            {
                Interlocked.Exchange(ref _reconnectActive, 0);
                return;
            }

            // 会话已重建：_gate 内有界等待后替换引用并核验订阅（防与 Disconnect 互等，D6）
            bool acquired;
            try { acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { acquired = false; }
            if (!acquired)
            {
                Interlocked.Exchange(ref _reconnectActive, 0);
                _logger.LogWarning("OPC UA 会话自愈回调等待闸门失败（驱动可能正在断开），放弃会话替换");
                try { newSession.Dispose(); } catch { }
                return;
            }
            try
            {
                if (_session is null || State != DriverState.Connected)
                {
                    _logger.LogInformation("OPC UA 会话自愈回调到达时驱动已断开/未连接，丢弃重建会话");
                    try { newSession.Dispose(); } catch { }
                    return;
                }

                _session = newSession;
                // 重绑 KeepAlive（幂等：SDK 重建若已克隆委托则换绑后无重复，D1/D6）
                BindKeepAlive(newSession);
                RealignSubscription(newSession);
                _logger.LogInformation("OPC UA 会话自愈成功：会话已重建并完成订阅核验");
            }
            finally
            {
                _gate.Release();
                Interlocked.Exchange(ref _reconnectActive, 0);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _reconnectActive, 0);
            _logger.LogError(ex, "处理 OPC UA 会话自愈完成回调失败");
        }
    }

    /// <summary>
    /// 会话重建后的订阅核验（ADR-072 D4）。SDK 的 <c>Session.Recreate</c> 已内置
    /// Transfer→Recreate 降级（禁止手写第二套迁移造成双 Transfer）；此处只做可观测核验：
    /// Transfer 成功保住的同一 <see cref="Subscription"/> 对象随迁到新会话（保持激活，
    /// 监控项 Handle/通知委托不变）；否则释放引用，交由既有订阅协调路径
    /// （<see cref="EnsureSubscriptionAsync"/>，幂等）重建。须在 <c>_gate</c> 内调用。
    /// </summary>
    private void RealignSubscription(Session newSession)
    {
        var subscription = _subscription;
        if (subscription is null)
            return;

        // 原订阅对象已随 Transfer 迁到新会话：保持激活即可
        if (ReferenceEquals(subscription.Session, newSession) || newSession.Subscriptions.Contains(subscription))
        {
            var monitoredCount = Enumerable.Count(subscription.MonitoredItems);
            _logger.LogInformation("OPC UA 会话重建后订阅已随 Transfer 迁移（{Count} 监控项）",
                monitoredCount);
            return;
        }

        // SDK 迁移未保住原订阅对象（如服务端不支持 Transfer → SDK 内部 Recreate 重建了新对象，
        // Handle/通知接续不可靠）：不复用内部重建对象，释放并交还订阅协调器重建（D4/D7）。
        _logger.LogWarning("OPC UA 会话重建后订阅未随 Transfer 迁移，将交由订阅协调器重建");
        _subscription = null;
        _subscriptionSignature = null;
        foreach (var item in subscription.MonitoredItems)
            item.Notification -= OnMonitoredItemNotification;
        try { subscription.DeleteAsync(false, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        subscription.Dispose();
    }

    /// <summary>
    /// ADR-072 D5：失败读/链路探测在自愈重连窗口内不置 <see cref="DriverState.Faulted"/>
    /// （保持 <c>Connected</c>，避免上层 ReliableProtocolDriver 整轮重建与自愈在同断点"双车抢道"）；
    /// 自愈结束（回调触发或取消）后再次失败才复位，把后续交给既有重试管线。
    /// </summary>
    internal void EnterFaultedIfNotSelfHealing()
    {
        if (Volatile.Read(ref _reconnectActive) == 0)
            State = DriverState.Faulted;
    }

    /// <summary>测试探针：是否处于自愈重连窗口（csproj InternalsVisibleTo 暴露给测试）。</summary>
    internal bool IsReconnectActiveForTesting => Volatile.Read(ref _reconnectActive) != 0;

    /// <summary>测试探针：置位/复位自愈防重入位（供无 SDK 会话的单测驱动 D3/D5 分支）。</summary>
    internal void SetReconnectActiveForTesting(bool active) =>
        Interlocked.Exchange(ref _reconnectActive, active ? 1 : 0);

    private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs args)
    {
        if (item.Handle is not DevicePoint point || args.NotificationValue is not DataValue value)
            return;
        if (!StatusCode.IsGood(value.StatusCode))
        {
            _logger.LogDebug("OPC UA 订阅点 {Point} 收到非 Good 状态 {StatusCode}，已跳过",
                point.Name, value.StatusCode);
            return;
        }

        var raw = new RawPointValue
        {
            Point = point,
            Value = VariantToValue(value.WrappedValue),
            Timestamp = value.SourceTimestamp == DateTime.MinValue ? DateTime.UtcNow : value.SourceTimestamp
        };
        _ = PublishValuesAsync([raw]);
    }

    private async Task PublishValuesAsync(IReadOnlyList<RawPointValue> values)
    {
        var handlers = ValuesReceived;
        if (handlers is null)
            return;
        foreach (Func<IReadOnlyList<RawPointValue>, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(values); }
            catch (Exception ex) { _logger.LogError(ex, "OPC UA 订阅值交付到采集管道失败"); }
        }
    }

    private static string BuildSubscriptionSignature(IReadOnlyList<DevicePoint> points, int publishingIntervalMs) =>
        $"{publishingIntervalMs}|{string.Join(';', points.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Address}:{p.ScanIntervalMs}"))}";

    /// <summary>
    /// ADR-073 D2/D3：GetEndpoints 拉取端点并按显式档位（策略/模式）手工选择。
    /// 命中返回 (Endpoint, null)；无匹配返回 (null, 错误消息，含可用端点清单)；发现/握手异常
    /// 原样抛出，由 <see cref="ConnectAsync"/> 统一映射（服务器不可达 → Timeout，服务级拒绝 → Communication）。
    /// </summary>
    private async Task<(EndpointDescription? Endpoint, string? Error)> DiscoverAndSelectEndpointAsync(
        ApplicationConfiguration config, OpcUaSecurityRequirement requirement, CancellationToken ct)
    {
        EndpointDescriptionCollection endpoints;
        var uri = new Uri(_connection.Endpoint);
        using (var discovery = await DiscoveryClient.CreateAsync(config, uri, DiagnosticsMasks.None, ct))
        {
            // SDK 的 GetEndpointsAsync 内部已按连接地址重写端点 URL（PatchEndpointUrls 为 SDK 私有，
            // 在服务调用内自动执行），返回的端点可直接用于建连。
            endpoints = await discovery.GetEndpointsAsync(profileUris: null, ct);
        }

        if (endpoints is null || endpoints.Count == 0)
            return (null, "OPC UA 端点发现未返回任何端点（GetEndpoints 结果为空）。");

        var selection = OpcUaSecurityParameters.SelectEndpoint(endpoints, requirement);
        if (selection.Endpoint is null)
        {
            _logger.LogWarning("OPC UA 无匹配端点: {Endpoint}\n{Error}", _connection.Endpoint, selection.Error);
            return (null, selection.Error);
        }

        _logger.LogDebug(
            "OPC UA 选中端点: {Url} 策略={Policy} 模式={Mode} 安全级别={Level}",
            selection.Endpoint.EndpointUrl,
            OpcUaSecurityParameters.PolicyDisplayName(selection.Endpoint.SecurityPolicyUri),
            selection.Endpoint.SecurityMode,
            selection.Endpoint.SecurityLevel);
        return (selection.Endpoint, null);
    }

    /// <summary>ADR-073 D4：按解析出的凭据构建用户身份；无凭据 → 匿名。</summary>
    private static UserIdentity BuildUserIdentity(OpcUaSecurityRequirement requirement) =>
        requirement.HasCredentials
            ? new UserIdentity(requirement.UserName!, Encoding.UTF8.GetBytes(requirement.Password!))
            : new UserIdentity();

    /// <summary>
    /// 构建客户端 ApplicationConfiguration。
    /// PKI 目录相对路径（opcua/pki/...）相对进程工作目录；信任状态以 pki 目录为唯一权威
    /// （ADR-073 D6/D8）：服务端证书只信任 opcua/pki/trusted 白名单内的项，未信任证书被拒绝
    /// （BadCertificateUntrusted）并落入 opcua/pki/rejected，由证书管理 API 移入 trusted 后重试。
    /// </summary>
    private ApplicationConfiguration BuildConfiguration(int requestTimeout)
    {
        var hostName = Dns.GetHostName();
        return new ApplicationConfiguration
        {
            ApplicationName = "NitroGateway",
            ApplicationUri = Utils.Format("urn:{0}:NitroGateway", hostName),
            ProductUri = "https://github.com/",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/own",
                    SubjectName = AppSubjectName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/trusted"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/issuers"
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/rejected"
                },
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = true,
                MinimumCertificateKeySize = 2048
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = requestTimeout
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = Math.Max(5000, requestTimeout)
            },
            CertificateValidator = new CertificateValidator()
        };
    }

    /// <summary>
    /// 空点位设备链路探测：读 ServerStatus 节点。
    /// 返回空列表表示链路正常（无点位数据），失败复位 Faulted 并返回 Timeout。
    /// </summary>
    private async Task<OperationResult<IReadOnlyList<RawPointValue>>> ProbeLinkAsync(CancellationToken ct)
    {
        try
        {
            var nodes = new ReadValueIdCollection
            {
                new() { NodeId = VariableIds.Server_ServerStatus, AttributeId = Attributes.Value }
            };
            var response = await _session!.ReadAsync(null, 0, TimestampsToReturn.Neither, nodes, ct);
            if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
                return Array.Empty<RawPointValue>();
            EnterFaultedIfNotSelfHealing();
            return OperationalError.Timeout("链路探测失败：ServerStatus 不可读");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EnterFaultedIfNotSelfHealing();
            return OperationalError.Timeout($"链路探测失败: {ex.Message}");
        }
    }

    /// <summary>OpcUaAddress → OPC UA NodeId（四型标识符映射）</summary>
    private static NodeId ToNodeId(OpcUaAddress addr) => addr switch
    {
        { StringId: { } s } => new NodeId(s, addr.NamespaceIndex),
        { NumericId: { } n } => new NodeId(n, addr.NamespaceIndex),
        { GuidId: { } g } => new NodeId(g, addr.NamespaceIndex),
        { OpaqueId: { } o } => new NodeId(o, addr.NamespaceIndex),
        _ => NodeId.Null
    };

    /// <summary>Variant → 领域值（int/float→double/bool/string 等）。null 回退 0.0（与 Modbus 失败读语义一致）</summary>
    private static object VariantToValue(Variant v) => v.Value switch
    {
        null => 0.0,
        sbyte sb => (short)sb,
        short s => s,
        int i => i,
        long l => l,
        ushort us => us,
        uint ui => ui,
        ulong ul => ul,
        float f => (double)f,
        double d => d,
        bool b => b,
        string str => str,
        _ => v.Value
    };

    /// <summary>领域值 → Variant（写路径），按点位声明的 <see cref="DataType"/> 强制类型化。</summary>
    /// <remarks>
    /// Float 用 <c>Convert.ToSingle</c>、Int64 用 <c>Convert.ToInt64</c> 等，不复用 Variant 默认的 .NET 类型：
    /// 否则 Float 点被 JSON 数值（一律为 double）写入时会发成 Double Variant → 服务端 BadTypeMismatch（ADR-019 实测）。
    /// 转换失败抛 <see cref="InvalidOperationException"/>，由 WriteAsync 上层捕获返回 Protocol 错误。
    /// </remarks>
    private static Variant ToVariant(DataType dataType, object value)
    {
        try
        {
            return dataType switch
            {
                DataType.Bool => new Variant(Convert.ToBoolean(value)),
                DataType.Byte => new Variant(Convert.ToByte(value)),
                DataType.Int16 => new Variant(Convert.ToInt16(value)),
                DataType.UInt16 => new Variant(Convert.ToUInt16(value)),
                DataType.Int32 => new Variant(Convert.ToInt32(value)),
                DataType.UInt32 => new Variant(Convert.ToUInt32(value)),
                DataType.Int64 => new Variant(Convert.ToInt64(value)),
                DataType.UInt64 => new Variant(Convert.ToUInt64(value)),
                DataType.Float => new Variant(Convert.ToSingle(value)),
                DataType.Double => new Variant(Convert.ToDouble(value)),
                DataType.String => new Variant(Convert.ToString(value) ?? string.Empty),
                _ => ToVariant(value)
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"写入值 '{value}' 无法转换为点位类型 {dataType}: {ex.Message}", ex);
        }
    }

    /// <summary>作为 <see cref="ToVariant(DataType, object)"/> 的兜底：按 .NET 实际类型映射（未声明的类型）。
    /// bool/string/数值直接映射，其余经 Convert.ToDouble 兜底。</summary>
    private static Variant ToVariant(object value) => value switch
    {
        bool b => new Variant(b),
        string s => new Variant(s),
        byte by => new Variant(by),
        sbyte sb => new Variant(sb),
        short s => new Variant(s),
        ushort us => new Variant(us),
        int i => new Variant(i),
        uint ui => new Variant(ui),
        long l => new Variant(l),
        ulong ul => new Variant(ul),
        float f => new Variant(f),
        double d => new Variant(d),
        _ => new Variant(Convert.ToDouble(value))
    };

    /// <summary>收集 Browse 结果中的引用（过滤 null 项）</summary>
    private static void CollectReferences(BrowseResult result, List<ReferenceDescription> references)
    {
        if (result is null || result.References is null) return;
        foreach (var r in result.References)
        {
            if (r is not null) references.Add(r);
        }
    }

    /// <summary>ExpandedNodeId → "ns=N;..." 格式（与 OpcUaAddressParser.Serialize 一致，可直接回填点位地址）</summary>
    private static string SerializeNodeId(ExpandedNodeId id)
    {
        if (id is null) throw new ArgumentException("浏览结果缺少 NodeId");
        var ns = id.NamespaceIndex;
        var identifier = id.Identifier;
        return identifier switch
        {
            string s => $"ns={ns};s={s}",
            uint u => $"ns={ns};i={u}",
            Guid g => $"ns={ns};g={g}",
            byte[] b => $"ns={ns};b={Convert.ToBase64String(b)}",
            _ => throw new ArgumentException($"不支持的 NodeId 标识符: {identifier}")
        };
    }

    /// <summary>DataType 属性 NodeId → 前端 DataType 枚举名（仅映射领域支持的 11 种，其余 Unknown）</summary>
    private static string DataTypeName(NodeId typeId)
    {
        if (typeId is null || typeId.IdType != IdType.Numeric || typeId.NamespaceIndex != 0)
            return "Unknown";
        if (typeId == DataTypeIds.Boolean) return "Bool";
        if (typeId == DataTypeIds.Byte) return "Byte";
        if (typeId == DataTypeIds.Int16) return "Int16";
        if (typeId == DataTypeIds.UInt16) return "UInt16";
        if (typeId == DataTypeIds.Int32) return "Int32";
        if (typeId == DataTypeIds.UInt32) return "UInt32";
        if (typeId == DataTypeIds.Int64) return "Int64";
        if (typeId == DataTypeIds.UInt64) return "UInt64";
        if (typeId == DataTypeIds.Float) return "Float";
        if (typeId == DataTypeIds.Double) return "Double";
        if (typeId == DataTypeIds.String) return "String";
        return "Unknown";
    }

    /// <summary>AccessLevel 属性 byte → "Read"/"ReadWrite"/"Write"/"None"</summary>
    private static string AccessToString(byte access)
    {
        var read = (AccessLevels.CurrentRead & access) != 0;
        var write = (AccessLevels.CurrentWrite & access) != 0;
        return read && write ? "ReadWrite" : read ? "Read" : write ? "Write" : "None";
    }
}
