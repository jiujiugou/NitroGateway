using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-027 P1-1：RealtimeViewModel 异步加载竞态——快速切换设备/点位时，
/// 旧查询结果晚到必须被版本守卫丢弃，不能覆盖新选择。
/// </summary>
public sealed class RealtimeViewModelTests : IDisposable
{
    /// <summary>帧间隔注入 1 小时，避免后台循环干扰（与 EventBridgeTests 同法）。</summary>
    private static readonly TimeSpan LongFrame = TimeSpan.FromHours(1);

    private readonly EventBridge _bridge;

    public RealtimeViewModelTests()
    {
        _bridge = new EventBridge(new StubForwardBuffer(), NullLogger<EventBridge>.Instance, LongFrame);
    }

    public void Dispose() => _bridge.Dispose();

    [Fact]
    public async Task LoadPointsAsync_stale_device_result_does_not_override_new_selection()
    {
        var cache = new StagedSnapshotCache();
        var deviceA = TestDevices.Device("A", TestDevices.Point("P1"), TestDevices.Point("P2"));
        var deviceB = TestDevices.Device("B", TestDevices.Point("Q1"));
        cache.EnqueueSuccess(deviceA, deviceB); // 构造时 LoadDevicesAsync

        // LoadPointsAsync(A) 与 LoadPointsAsync(B) 均挂起，模拟慢查询
        var gateA = new TaskCompletionSource<OperationResult<IReadOnlyList<Device>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource<OperationResult<IReadOnlyList<Device>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Enqueue(gateA.Task);
        cache.Enqueue(gateB.Task);

        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        vm.SelectedDevice = new DeviceOption(deviceA.Id, deviceA.Name);
        vm.SelectedDevice = new DeviceOption(deviceB.Id, deviceB.Name);

        // B 先完成，A 后完成（乱序晚到）
        gateB.SetResult(OperationResult<IReadOnlyList<Device>>.Success([deviceB]));
        gateA.SetResult(OperationResult<IReadOnlyList<Device>>.Success([deviceA]));

        await TestWait.UntilAsync(() => vm.Points.Count == deviceB.Points.Count);
        await Task.Delay(50); // 给 A 的晚到回调留出执行时间

        Assert.Equal(deviceB.Points.Count, vm.Points.Count);
        Assert.Equal("Q1", vm.Points[0].Name);
        Assert.Equal($"设备「B」共 1 个点位", vm.StatusText);
    }

    [Fact]
    public async Task LoadPointHistoryAsync_stale_point_result_does_not_override_new_point()
    {
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"), TestDevices.Point("P2"));
        cache.EnqueueSuccess(device);
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)

        var store = new StagedMeasurementStore();
        var gate1 = new TaskCompletionSource<OperationResult<IReadOnlyList<PointSnapshot>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource<OperationResult<IReadOnlyList<PointSnapshot>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.EnqueuePaged(gate1.Task); // LoadPointHistoryAsync(P1)
        store.EnqueuePaged(gate2.Task); // LoadPointHistoryAsync(P2)

        var vm = new RealtimeViewModel(cache, store, new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        var point1 = device.Points.First();
        var point2 = device.Points.Last();
        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == device.Points.Count);

        vm.SelectedPoint = vm.Points.First(p => p.PointId == point1.Id);
        vm.SelectedPoint = vm.Points.First(p => p.PointId == point2.Id);

        // P2 先完成，P1 后完成（乱序晚到）
        gate2.SetResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(device.Id, point2.Id, 22.0)]));
        gate1.SetResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(device.Id, point1.Id, 11.0)]));

        await TestWait.UntilAsync(() => vm.ChartValues.Count == 1);
        await Task.Delay(50); // 给 P1 的晚到回调留出执行时间

        Assert.Equal(1, vm.ChartValues.Count);
        Assert.Equal(22.0, vm.ChartValues[0].Value);
    }

    [Fact]
    public async Task Chart_keeps_at_most_two_hour_window_of_points()
    {
        // ADR-037 S9/S12：环形缓冲上限 = 7200（1s 采集 2 小时，与预载窗口一致），
        // 溢出批量移除，帧追加不逐项搬移
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)
        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        vm.SelectedPoint = vm.Points[0];
        var point = device.Points.First();

        for (var i = 0; i < 7300; i++)
        {
            await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
            {
                DeviceId = device.Id,
                Snapshots = [TestDevices.Snapshot(device.Id, point.Id, i)]
            });
            _bridge.Flush();
        }

        Assert.Equal(7200, vm.ChartValues.Count);
        Assert.Equal(7299d, vm.ChartValues[^1].Value);
    }
}
