using System.Net;
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
/// v1 轮询模式；v2 再评估 Subscription + Browse（<see cref="OpcUaDriverCapability"/> 的
/// <c>SupportsSubscription=true</c> 为能力预留，采集引擎仍走轮询）。
/// </summary>
/// <remarks>
/// <para><b>并发闸门（ADR-019 P2-1）：</b>OPC UA Session 非线程安全，全部通信（读/写/连接/断开/Ping）
/// 经 <see cref="_gate"/> 串行化，防止 1s 采集读 + Webapi 写 + 健康 Ping 并发访问同一 Session 导致
/// 请求交错/协议失步（与 Modbus/S7 驱动同一约束）。</para>
/// <para><b>失败读不产伪值（ADR-019 P1-1）：</b>Read 响应显式检查 <c>StatusCode</c>，
/// Bad/Uncertain 状态跳过该点位（SDK 在 Bad 时 <c>WrappedValue</c> 为默认值，直接取会把故障读当作
/// 0.0 + Good 写入时序库并上云）；全部失败复位 <see cref="DriverState.Faulted"/>，让上层重试管线重新建连。</para>
/// <para><b>应用证书尽力而为：</b>首次连接尝试在 <c>opcua/pki/own</c> 生成应用证书（SubjectName
/// <c>CN=NitroGateway</c>）；生成失败降级为 None 安全策略 + 匿名身份（无需客户端证书即可连通演示服务器）。
/// 服务端证书一律自动接受（<c>AutoAcceptUntrustedCertificates=true</c> + 校验回调 Accept），
/// 适合内网演示；现场生产应改为信任库白名单校验。</para>
/// </remarks>
public sealed class OpcUaDriver : IProtocolDriver, IDisposable
{
    /// <summary>应用证书 SubjectName；首次连接自动生成到 opcua/pki/own 目录存储</summary>
    private const string AppSubjectName = "CN=NitroGateway, DC=localhost";

    private readonly DeviceConnection _connection;
    private readonly ILogger _logger;
    private readonly OpcUaAddressParser _addressParser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前会话；null 表示未连接。会话非线程安全，全部通信经 <see cref="_gate"/> 串行化</summary>
    private Session? _session;

    /// <summary>是否已就绪应用证书；false 时仅选择 None 安全策略端点（匿名身份无需客户端证书）</summary>
    private bool _hasAppCertificate;

    /// <inheritdoc />
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc />
    public DriverCapability Capability => OpcUaDriverCapability.Instance;

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

            State = DriverState.Connecting;
            ct.ThrowIfCancellationRequested();

            try
            {
                // ADR-019 P2-4：操作超时与设备请求超时对齐（取 RequestTimeoutMs，下限 1s）
                var requestTimeout = Math.Max(1000, _connection.RequestTimeoutMs);

                // 1) 程序化构建 ApplicationConfiguration（不依赖 XML 配置文件，SDK 1.5 支持直接构造）
                var config = BuildConfiguration(requestTimeout);
                await config.Validate(ApplicationType.Client);
                // 服务端证书一律接受（演示/内网；现场可改为按信任库白名单校验）
                config.CertificateValidator!.CertificateValidation += (_, e) => e.Accept = true;

                // 2) 应用证书：尽力而为。生成失败降级 false → 走 None 安全策略 + 匿名，仍可连通演示服务器
                try
                {
                    var app = new ApplicationInstance
                    {
                        ApplicationName = "NitroGateway",
                        ApplicationType = ApplicationType.Client,
                        ApplicationConfiguration = config
                    };
                    _hasAppCertificate = await app.CheckApplicationInstanceCertificates(silent: true);
                }
                catch (Exception ex)
                {
                    _hasAppCertificate = false;
                    _logger.LogDebug("应用证书不可用，将使用无加密（None）连接: {Error}", ex.Message);
                }

                // 3) 选端点：优先安全端点；无应用证书时强制 None，避免带证书握手失败
                EndpointDescription selected;
                try
                {
                    selected = CoreClientUtils.SelectEndpoint(config, _connection.Endpoint, useSecurity: _hasAppCertificate);
                }
                catch
                {
                    // 安全端点发现失败（如服务器不支持加密/证书不被接受）→ 回退 None 端点
                    selected = CoreClientUtils.SelectEndpoint(config, _connection.Endpoint, useSecurity: false);
                }

                // 4) 建会话（匿名身份 v1；updateBeforeConnect=false 不重复发现）
                var configuredEndpoint = new ConfiguredEndpoint(selected.Server, EndpointConfiguration.Create(config));
                configuredEndpoint.Update(selected);
                var session = await Session.Create(
                    config,
                    configuredEndpoint,
                    updateBeforeConnect: false,
                    checkDomain: false,
                    "NitroGateway",
                    (uint)Math.Max(5000, requestTimeout),
                    new UserIdentity(),
                    null,
                    ct);

                ct.ThrowIfCancellationRequested();
                _session = session;
                State = DriverState.Connected;
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

            // ADR-019 P3-1：全部失败复位 Faulted，让重试管线重新建连（与 Modbus/S7 对齐）
            if (results.Count == 0)
            {
                State = DriverState.Faulted;
                return OperationalError.Protocol($"批量读取失败：{validPoints.Count} 个点位均未返回数据");
            }

            if (results.Count < validPoints.Count)
                _logger.LogWarning("批量读取部分失败：{Ok}/{Total} 个点位成功", results.Count, validPoints.Count);

            return results;
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
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
    public void Dispose()
    {
        try { _session?.CloseSession(null, true); } catch { }
        _session?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// 构建客户端 ApplicationConfiguration。
    /// PKI 目录相对路径（opcua/pki/...）相对进程工作目录；内网演示用目录存储 + 自动接受，
    /// 生产应改为 Windows 证书库 + 信任白名单。
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
                AutoAcceptUntrustedCertificates = true,
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
            State = DriverState.Faulted;
            return OperationalError.Timeout("链路探测失败：ServerStatus 不可读");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
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
}
