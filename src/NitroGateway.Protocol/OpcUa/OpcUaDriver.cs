using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace NitroGateway.Protocol.OpcUa;

/// <summary>
/// OPC UA 协议驱动 v1。基于 OPC Foundation .NET SDK。
/// Session：Connect → Read/Write → Disconnect。
/// v1 轮询模式，v2 加 Subscription + Browse。
/// </summary>
public sealed class OpcUaDriver : IProtocolDriver, IDisposable
{
    private readonly DeviceConnection _connection;
    private readonly ILogger<OpcUaDriver> _logger;
    private readonly OpcUaAddressParser _addressParser = new();
    private ApplicationInstance? _app;
    private Session? _session;

    /// <inheritdoc />
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc />
    public DriverCapability Capability => OpcUaDriverCapability.Instance;

    public OpcUaDriver(DeviceConnection connection, ILogger<OpcUaDriver> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        if (State == DriverState.Connected) return OperationResult.Success();
        State = DriverState.Connecting;

        try
        {
            _app = new ApplicationInstance { ApplicationName = "NitroGateway", ApplicationType = ApplicationType.Client };
            var config = await _app.LoadApplicationConfiguration(silent: false);
            config.SecurityConfiguration!.AutoAcceptUntrustedCertificates = true;
            await config.Validate(ApplicationType.Client);
            config.CertificateValidator!.CertificateValidation += (_, _) => { };

            // TODO: OPC UA SDK 1.5 API 确认 — 需对着 Prosys 实测
            // var selectedEndpoint = CoreClientUtils.SelectEndpoint(_connection.Endpoint);
            // _session = await Session.Create(config, selectedEndpoint, false, "NitroGateway", 60000, null, null, ct);
            throw new NotImplementedException("OPC UA Connect 需对着 Prosys / UA Expert 实测调通 SDK API");

            State = DriverState.Connected;
            _logger.LogInformation("OPC UA 已连接: {Endpoint}", _connection.Endpoint);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            State = DriverState.Faulted;
            return OperationalError.Timeout($"OPC UA 连接失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        try { _session?.Close(); _session?.Dispose(); } catch { }
        _session = null; State = DriverState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    /// <inheritdoc />
    public async Task<OperationResult> PingAsync(CancellationToken ct = default)
    {
        if (_session is null) return OperationalError.Unavailable("OPC UA 未连接");
        try { await _session.ReadValueAsync(VariableIds.Server_ServerStatus, ct); return OperationResult.Success(); }
        catch (Exception ex) { return OperationalError.Timeout($"Ping 失败: {ex.Message}"); }
    }

    /// <inheritdoc />
    public async Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
    {
        var result = await ReadBatchAsync([point], ct);
        if (result.IsFailure) return result.Error!;
        var first = result.Value!.FirstOrDefault();
        return first is not null ? OperationResult<RawPointValue>.Success(first) : OperationalError.Protocol($"读取失败: {point.Name}");
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
    {
        if (_session is null || State != DriverState.Connected) return OperationalError.Unavailable("OPC UA 未连接");
        var pointList = points.ToList();
        if (pointList.Count == 0) return Array.Empty<RawPointValue>();
        var results = new List<RawPointValue>();

        try
        {
            var nodesToRead = new ReadValueIdCollection();
            foreach (var p in pointList)
            {
                var uaAddr = (OpcUaAddress)_addressParser.Parse(p.Address);
                nodesToRead.Add(new ReadValueId { NodeId = ToNodeId(uaAddr), AttributeId = Attributes.Value });
            }

            var response = await _session.ReadAsync(null, 0, TimestampsToReturn.Both, nodesToRead, ct);

            for (var i = 0; i < pointList.Count && i < response.Results.Count; i++)
            {
                var dv = response.Results[i];
                var value = VariantToValue(dv.WrappedValue);
                results.Add(new RawPointValue
                {
                    Point = pointList[i], Value = value,
                    Timestamp = dv.SourceTimestamp == DateTime.MinValue ? DateTime.UtcNow : dv.SourceTimestamp
                });
            }
        }
        catch (Exception ex) { return OperationalError.Protocol($"OPC UA 读失败: {ex.Message}"); }

        return results;
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
    {
        if (_session is null) return OperationalError.Unavailable("OPC UA 未连接");
        try
        {
            var uaAddr = (OpcUaAddress)_addressParser.Parse(point.Address);
            var variant = value switch { double d => new Variant(d), float f => new Variant(f), int i => new Variant(i), bool b => new Variant(b), string s => new Variant(s), _ => new Variant(Convert.ToDouble(value)) };
            var dv = new DataValue(variant);
            var nodesToWrite = new WriteValueCollection
            {
                new() { NodeId = ToNodeId(uaAddr), AttributeId = Attributes.Value, Value = dv }
            };
            var response = await _session.WriteAsync(null, nodesToWrite, ct);
            return StatusCode.IsGood(response.Results[0]) ? OperationResult.Success() : OperationalError.Protocol($"写入失败: {response.Results[0]}");
        }
        catch (Exception ex) { return OperationalError.Protocol($"写入失败: {ex.Message}"); }
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
    {
        foreach (var (p, v) in entries) { var r = await WriteAsync(p, v, ct); if (r.IsFailure) return r; }
        return OperationResult.Success();
    }

    public void Dispose() { _session?.Dispose(); }

    private static NodeId ToNodeId(OpcUaAddress addr) => addr switch
    {
        { StringId: { } s } => new NodeId(s, addr.NamespaceIndex),
        { NumericId: { } n } => new NodeId(n, addr.NamespaceIndex),
        { GuidId: { } g } => new NodeId(g, addr.NamespaceIndex),
        { OpaqueId: { } o } => new NodeId(o, addr.NamespaceIndex),
        _ => NodeId.Null
    };

    private static object VariantToValue(Variant v) => v.Value switch
    {
        null => 0.0, float f => (double)f, short s => s,
        int i => i, double d => d, bool b => b, string str => str, _ => v.Value
    };
}
