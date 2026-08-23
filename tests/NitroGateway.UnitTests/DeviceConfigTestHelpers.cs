using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Desktop.Services.Dialogs;
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

    /// <summary>GetAllAsync 返回值（ADR-033 导入测试用）；缺省空列表</summary>
    public IReadOnlyList<Device> LocalDevices { get; set; } = Array.Empty<Device>();

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
        Task.FromResult(GetResult is null
            ? OperationResult<Device>.Failure(OperationalError.General("设备不存在"))
            : OperationResult<Device>.Success(GetResult));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(LocalDevices));
    public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default) => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(LocalDevices));
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default)
        => GetAllAsync(ct);
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default)
        => GetAllIncludingDeletedAsync(ct);
    public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(GetResult is null
            ? OperationResult<Device>.Failure(OperationalError.General("设备不存在"))
            : OperationResult<Device>.Success(GetResult));
    public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default) { Unregistered.Add(deviceId); return Task.FromResult(OperationResult.Success()); }
}

/// <summary>ADR-029 测试替身：记录调用的点位管理器。</summary>
internal sealed class StubPointManager : IPointManager
{
    public List<DevicePoint> Points { get; } = [];
    public List<(Guid DeviceId, DevicePoint Point)> Added { get; } = [];
    public List<(Guid DeviceId, DevicePoint Point)> Updated { get; } = [];
    public List<(Guid DeviceId, Guid PointId)> Removed { get; } = [];
    public List<(Guid DeviceId, IReadOnlyList<DevicePoint> Points)> Imported { get; } = [];

    private readonly Dictionary<Guid, Guid> _pointDevice = new();

    /// <summary>测试直接预置设备点位（不记录调用）。</summary>
    public void Seed(Guid deviceId, params DevicePoint[] points)
    {
        foreach (var point in points)
        {
            Points.Add(point);
            _pointDevice[point.Id] = deviceId;
        }
    }

    public Task<OperationResult<DevicePoint>> AddAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        Added.Add((deviceId, point));
        Seed(deviceId, point);
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
        _pointDevice.Remove(pointId);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(OperationResult<IReadOnlyList<DevicePoint>>.Success(
            Points.Where(p => _pointDevice.TryGetValue(p.Id, out var owner) && owner == deviceId).ToArray()));

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        Imported.Add((deviceId, points));
        Seed(deviceId, points.ToArray());
        return Task.FromResult(OperationResult<IReadOnlyList<DevicePoint>>.Success(points));
    }
    public Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>ADR-029 测试替身：对话框（可编程结果 + 记录调用）。</summary>
internal sealed class StubDeviceDialogService : IDeviceDialogService
{
    public bool EditDeviceResult = true;
    public bool EditPointResult = true;
    public bool EditPointBatchResult = true;
    public bool EditWriteResult = true;
    public bool ConfirmResult = true;
    public string? EditDeviceFillName;
    public string? EditPointFillName;
    public string? EditPointBatchFillNameTemplate;
    public string? EditPointBatchFillStartAddress;
    public int? EditPointBatchFillCount;
    public string? EditPointBatchFillProtocol;
    public int EditDeviceCalls;
    public int EditPointCalls;
    public int EditPointBatchCalls;
    public int EditWriteCalls;
    public int ConfirmCalls;
    public List<(Guid DeviceId, string DeviceName, string ProtocolName)> ShowPointsCalls { get; } = [];

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

    public bool EditPointBatch(PointBatchEditor editor)
    {
        EditPointBatchCalls++;
        if (EditPointBatchFillNameTemplate is not null)
            editor.NameTemplate = EditPointBatchFillNameTemplate;
        if (EditPointBatchFillStartAddress is not null)
            editor.StartAddress = EditPointBatchFillStartAddress;
        if (EditPointBatchFillCount is not null)
            editor.Count = EditPointBatchFillCount.Value;
        if (EditPointBatchFillProtocol is not null)
            editor.ProtocolName = EditPointBatchFillProtocol;
        return EditPointBatchResult;
    }

    public bool EditWrite(WriteValueEditor editor)
    {
        EditWriteCalls++;
        return EditWriteResult;
    }

    public bool Confirm(string title, string message)
    {
        ConfirmCalls++;
        return ConfirmResult;
    }

    public void ShowPoints(Guid deviceId, string deviceName, string protocolName) =>
        ShowPointsCalls.Add((deviceId, deviceName, protocolName));
}

/// <summary>CSV 文件服务测试替身：可编程内容/结果 + 记录调用（点位导入导出用）。</summary>
internal sealed class StubCsvFileService : ICsvFileService
{
    /// <summary>PickImportCsv 返回值；null 表示用户取消</summary>
    public string? PickImportResult;

    /// <summary>SaveCsv 返回值；false 表示用户取消</summary>
    public bool SaveResult = true;

    public string? LastSavedFileName;
    public string? LastSavedContent;
    public int PickCalls;
    public int SaveCalls;

    public string? PickImportCsv()
    {
        PickCalls++;
        return PickImportResult;
    }

    public bool SaveCsv(string defaultFileName, string content)
    {
        SaveCalls++;
        LastSavedFileName = defaultFileName;
        LastSavedContent = content;
        return SaveResult;
    }
}

/// <summary>ADR-029 测试替身：健康监控（无快照）。</summary>
internal sealed class StubHealthMonitor : IDeviceHealthMonitor
{
    public int FailureThreshold => 3;
    public int RecoveryThreshold => 3;
    public void ReportSuccess(Guid deviceId, string? deviceName) { }
    public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
    public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
    public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
    public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
    public void Remove(Guid deviceId) { }
    public void AddListener(IDeviceHealthListener listener) { }
}

/// <summary>ADR-044 测试替身：可编程连接测试结果 + 记录被测试设备。</summary>
internal sealed class StubConnectionTester : IDeviceConnectionTester
{
    /// <summary>TestAsync 返回值（可编程）。</summary>
    public ConnectionTestResult Result { get; set; } = new(true, 12, null, "ok");

    /// <summary>非 null 时挂起，直到该任务完成（模拟测试进行中）。</summary>
    public Task<ConnectionTestResult>? Gate { get; set; }

    /// <summary>TestAsync 收到的设备对象。</summary>
    public List<Device> Calls { get; } = [];

    /// <summary>最后一次被测试的设备（无调用时为 null）。</summary>
    public Device? LastDevice => Calls.Count == 0 ? null : Calls[^1];

    public async Task<ConnectionTestResult> TestAsync(Device device, CancellationToken ct = default)
    {
        Calls.Add(device);
        return Gate is null ? Result : await Gate;
    }
}

