using Microsoft.AspNetCore.Mvc;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Storage.Buffer;
using NitroGateway.Webapi.Controllers;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Services;
using AlarmRuleDomain = NitroGateway.Alarm.Domain.AlarmRule;

namespace NitroGateway.UnitTests;

public sealed class FakeDeviceManager : IDeviceManager
{
    public Device? LastRegistered { get; private set; }

    /// <summary>GetAllAsync 返回值（ADR-033 导出测试用）；缺省空列表</summary>
    public IReadOnlyList<Device> AllDevices { get; set; } = Array.Empty<Device>();

    public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
    {
        LastRegistered = device;
        return Task.FromResult(OperationResult<Device>.Success(device));
    }

    public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Failure(OperationalError.NotFound("设备不存在")));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(AllDevices));

    public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());

    public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(AllDevices.ToList()));
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default)
        => GetAllAsync(ct);
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default)
        => GetAllIncludingDeletedAsync(ct);
    public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Success(AllDevices.FirstOrDefault(d => d.Id == deviceId)));
    public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = AllDevices.FirstOrDefault(d => d.Id == deviceId);
        if (device is not null)
        {
            device.IsDeleted = true;
            device.UpdatedAt = DateTime.UtcNow;
        }
        return Task.FromResult(OperationResult.Success());
    }
}

public sealed class FakePointManager : IPointManager
{
    public DevicePoint? LastAdded { get; private set; }

    public Task<OperationResult<DevicePoint>> AddAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        LastAdded = point;
        return Task.FromResult(OperationResult<DevicePoint>.Success(point));
    }

    public Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> UpdateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<DevicePoint>>.Success(points));

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<DevicePoint>>>(Array.Empty<DevicePoint>());

    public Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<PointValidationError>>>(Array.Empty<PointValidationError>());
}

public sealed class FakeHealthMonitor : IDeviceHealthMonitor
{
    public void ReportSuccess(Guid deviceId, string? deviceName) { }
    public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
    public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
    public int FailureThreshold => 3;
    public int RecoveryThreshold => 3;
    public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
    public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
    public void Remove(Guid deviceId) { }
    public void AddListener(IDeviceHealthListener listener) { }
}

public sealed class FakeDriverFactory : IProtocolDriverFactory
{
    public FakeDriverFactory(IProtocolDriver? driver = null) => Driver = driver;

    public IProtocolDriver? Driver { get; }

    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
        => Driver ?? throw new NotImplementedException("连接测试用例需要注入 FakeProtocolDriver");
}

public sealed class FakeSerialPorts : ISerialPortManager
{
    public SerialPortLease Acquire(SerialPortSettings settings)
        => throw new NotImplementedException("串口用例不需要真实租约");
    public IReadOnlyList<string> GetAvailablePorts() => [];
    public IReadOnlyList<SerialPortInfo> GetStatus() => [];
}

public sealed class FakeSiteIdProvider : ISiteIdProvider
{
    public string Current { get; set; } = "test-site";
    public SiteIdSource Source { get; set; } = SiteIdSource.Persisted;

    public OperationResult Save(string siteId)
    {
        // 与真实 SiteIdProvider 一致：先校验格式（供非法输入→400 用例）
        if (!SiteOptions.IsValidSiteId(siteId))
            return OperationResult.Failure(OperationalError.Validation("站点标识不合法"));
        Current = siteId;
        Source = SiteIdSource.Persisted;
        return OperationResult.Success();
    }

    public string Regenerate()
    {
        Current = "test-site-2";
        Source = SiteIdSource.Persisted;
        return Current;
    }
}

public sealed class FakeConfigSyncOutboxStore : IConfigSyncOutboxStore
{
    public int RecordDeviceCalls { get; private set; }
    public int RecordDeviceDeleteCalls { get; private set; }
    public int RecordPointCalls { get; private set; }
    public int RecordPointDeleteCalls { get; private set; }

    public Task<OperationResult> RecordDeviceAsync(Device device, CancellationToken ct = default)
    { RecordDeviceCalls++; return Task.FromResult(OperationResult.Success()); }

    public Task<OperationResult> RecordDeviceDeleteAsync(Guid deviceId, CancellationToken ct = default)
    { RecordDeviceDeleteCalls++; return Task.FromResult(OperationResult.Success()); }

    public Task<OperationResult> RecordPointAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    { RecordPointCalls++; return Task.FromResult(OperationResult.Success()); }

    public Task<OperationResult> RecordPointDeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    { RecordPointDeleteCalls++; return Task.FromResult(OperationResult.Success()); }

    public Task<OperationResult<IReadOnlyList<ConfigSyncOutboxRow>>> GetPendingAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<ConfigSyncOutboxRow>>.Success(Array.Empty<ConfigSyncOutboxRow>()));

    public Task<OperationResult> ClearAsync(ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId = null, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> ClearForDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> ClearAllAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}
public sealed class FakeForwardBuffer : IForwardBuffer
{
    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<BatchMeasurements>>>(Array.Empty<BatchMeasurements>());

    public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<DeadLetterEntry>>>(Array.Empty<DeadLetterEntry>());

    public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public int Count => 0;

    public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(0);
}

public sealed class FakeAlarmRuleRepository : IAlarmRuleRepository
{
    public AlarmRuleDomain? LastSaved { get; private set; }

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetByPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetAllIncludingDisabledAsync(CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult> SaveAsync(AlarmRuleDomain rule, CancellationToken ct = default)
    {
        LastSaved = rule;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}
public sealed class FakeProtocolDriver : IProtocolDriver
{
    private readonly OperationResult _connectResult;
    private readonly OperationResult _pingResult;

    public FakeProtocolDriver(OperationResult connectResult, OperationResult pingResult)
    {
        _connectResult = connectResult;
        _pingResult = pingResult;
    }

    public DriverState State => DriverState.Connected;
    public DriverCapability Capability => new();

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        => Task.FromResult(_connectResult);

    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> PingAsync(CancellationToken ct = default)
        => Task.FromResult(_pingResult);

    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));

    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public void Dispose() { }
}




