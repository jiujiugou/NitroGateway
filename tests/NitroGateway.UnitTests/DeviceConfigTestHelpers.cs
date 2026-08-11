using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.UnitTests;

/// <summary>ADR-029 测试替身：记录调用的设备管理器。</summary>
internal sealed class StubDeviceManager : IDeviceManager
{
    public List<Device> Registered { get; } = [];
    public List<Guid> Unregistered { get; } = [];
    public Device? GetResult { get; set; }
    public bool FailNextRegister { get; set; }

    public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
    {
        if (FailNextRegister)
        {
            FailNextRegister = false;
            return Task.FromResult(OperationResult<Device>.Failure(OperationalError.Validation("设备参数不合法")));
        }
        Registered.Add(device);
        return Task.FromResult(OperationResult<Device>.Success(device));
    }

    public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
    {
        Unregistered.Add(deviceId);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(OperationResult<Device>.Success(GetResult));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>ADR-029 测试替身：记录调用的点位管理器。</summary>
internal sealed class StubPointManager : IPointManager
{
    public List<DevicePoint> Points { get; } = [];
    public List<(Guid DeviceId, DevicePoint Point)> Added { get; } = [];
    public List<(Guid DeviceId, DevicePoint Point)> Updated { get; } = [];
    public List<(Guid DeviceId, Guid PointId)> Removed { get; } = [];

    public Task<OperationResult<DevicePoint>> AddAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        Added.Add((deviceId, point));
        Points.Add(point);
        return Task.FromResult(OperationResult<DevicePoint>.Success(point));
    }

    public Task<OperationResult> UpdateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        Updated.Add((deviceId, point));
        var index = Points.FindIndex(p => p.Id == point.Id);
        if (index >= 0)
            Points[index] = point; // 贴近真实仓储：更新后列表反映新值
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        Removed.Add((deviceId, pointId));
        Points.RemoveAll(p => p.Id == pointId);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<DevicePoint>>.Success(Points.ToArray()));

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>ADR-029 测试替身：对话框（可编程结果 + 记录调用）。</summary>
internal sealed class StubDeviceDialogService : IDeviceDialogService
{
    public bool EditDeviceResult = true;
    public bool EditPointResult = true;
    public bool ConfirmResult = true;
    public string? EditDeviceFillName;
    public string? EditPointFillName;
    public int EditDeviceCalls;
    public int EditPointCalls;
    public int ConfirmCalls;
    public List<(Guid DeviceId, string DeviceName)> ShowPointsCalls { get; } = [];

    public bool EditDevice(DeviceEditor editor)
    {
        EditDeviceCalls++;
        if (EditDeviceFillName is not null)
            editor.Name = EditDeviceFillName;
        return EditDeviceResult;
    }

    public bool EditPoint(PointEditor editor)
    {
        EditPointCalls++;
        if (EditPointFillName is not null)
            editor.Name = EditPointFillName;
        return EditPointResult;
    }

    public bool Confirm(string title, string message)
    {
        ConfirmCalls++;
        return ConfirmResult;
    }

    public void ShowPoints(Guid deviceId, string deviceName) => ShowPointsCalls.Add((deviceId, deviceName));
}

/// <summary>ADR-029 测试替身：健康监控（无快照）。</summary>
internal sealed class StubHealthMonitor : IDeviceHealthMonitor
{
    public int FailureThreshold => 3;
    public int RecoveryThreshold => 3;
    public void ReportSuccess(Guid deviceId) { }
    public void ReportFailure(Guid deviceId, string reason) { }
    public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
    public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
    public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
    public void Remove(Guid deviceId) { }
    public void AddListener(IDeviceHealthListener listener) { }
}
