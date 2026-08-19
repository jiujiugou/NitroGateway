using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Infrastructure;

using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-027 P2-1/P2-3/P3-6：HistoryViewModel 竞态守卫、分页 offset 与日期可空校验。
/// </summary>
public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task LoadPointsAsync_stale_device_result_does_not_override_new_selection()
    {
        var cache = new StagedSnapshotCache();
        var deviceA = TestDevices.Device("A", TestDevices.Point("P1"), TestDevices.Point("P2"));
        var deviceB = TestDevices.Device("B", TestDevices.Point("Q1"));
        cache.EnqueueSuccess(deviceA, deviceB); // 构造时 LoadDevicesAsync

        var gateA = new TaskCompletionSource<OperationResult<IReadOnlyList<Device>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource<OperationResult<IReadOnlyList<Device>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Enqueue(gateA.Task); // LoadPointsAsync(A)
        cache.Enqueue(gateB.Task); // LoadPointsAsync(B)

        var vm = new HistoryViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            NullLogger<HistoryViewModel>.Instance);

        vm.SelectedDevice = new DeviceOption(deviceA.Id, deviceA.Name);
        vm.SelectedDevice = new DeviceOption(deviceB.Id, deviceB.Name);

        gateB.SetResult(OperationResult<IReadOnlyList<Device>>.Success([deviceB]));
        gateA.SetResult(OperationResult<IReadOnlyList<Device>>.Success([deviceA]));

        await TestWait.UntilAsync(() => vm.Points.Count == deviceB.Points.Count);
        await Task.Delay(50); // 给 A 的晚到回调留出执行时间

        Assert.Equal(deviceB.Points.Count, vm.Points.Count);
        Assert.Equal("Q1", vm.Points[0].Name);
    }

    [Fact]
    public async Task QueryAsync_stale_result_is_discarded_when_device_switched_mid_query()
    {
        var cache = new StagedSnapshotCache();
        var deviceA = TestDevices.Device("A", TestDevices.Point("P1"));
        var deviceB = TestDevices.Device("B", TestDevices.Point("Q1"));
        cache.EnqueueSuccess(deviceA, deviceB); // 构造时 LoadDevicesAsync
        cache.EnqueueSuccess(deviceA);          // LoadPointsAsync(A)
        cache.EnqueueSuccess(deviceB);          // LoadPointsAsync(B)

        var store = new StagedMeasurementStore();
        var gate = new TaskCompletionSource<OperationResult<IReadOnlyList<PointSnapshot>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.EnqueuePaged(gate.Task); // 查询 A（挂起）

        var vm = new HistoryViewModel(cache, store, new UiDispatcher(),
            NullLogger<HistoryViewModel>.Instance);

        vm.SelectedDevice = new DeviceOption(deviceA.Id, deviceA.Name);
        vm.SelectedPoint = new PointOption(deviceA.Points.First().Id, "P1", "40001");
        var queryTask = vm.QueryCommand.ExecuteAsync(null); // 在途

        // 查询未返回时切换设备：版本号递增，旧查询结果到达后必须被丢弃
        vm.SelectedDevice = new DeviceOption(deviceB.Id, deviceB.Name);

        gate.SetResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(deviceA.Id, deviceA.Points.First().Id, 1.0)]));
        await queryTask;
        await Task.Delay(50);

        Assert.Empty(vm.Rows);
        Assert.NotEqual("第 1 页", vm.StatusText);
    }

    [Fact]
    public async Task QueryAsync_is_not_reentrant_while_in_flight()
    {
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device);
        cache.EnqueueSuccess(device);

        var store = new StagedMeasurementStore();
        var gate = new TaskCompletionSource<OperationResult<IReadOnlyList<PointSnapshot>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.EnqueuePaged(gate.Task);

        var vm = new HistoryViewModel(cache, store, new UiDispatcher(),
            NullLogger<HistoryViewModel>.Instance);
        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        vm.SelectedPoint = new PointOption(device.Points.First().Id, "P1", "40001");

        var first = vm.QueryCommand.ExecuteAsync(null);
        var second = vm.QueryCommand.ExecuteAsync(null); // 在途时重入应直接返回
        await second;

        // ADR-047：store 查询经 Task.Run 移到线程池，等待第一次查询真正出队后再断言只触达一次
        await TestWait.UntilAsync(() => store.PagedDequeueCount >= 1);
        Assert.Single(store.PagedCalls); // 第二次查询未再触达存储

        gate.SetResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));
        await first;
    }

    [Fact]
    public async Task Pagination_uses_incrementing_offset_and_tracks_can_go()
    {
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device);
        cache.EnqueueSuccess(device);

        var store = new StagedMeasurementStore();
        store.EnqueuePaged(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            Enumerable.Range(0, 1000).Select(i =>
                TestDevices.Snapshot(device.Id, device.Points.First().Id, i)).ToArray())));
        store.EnqueuePaged(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            Enumerable.Range(0, 100).Select(i =>
                TestDevices.Snapshot(device.Id, device.Points.First().Id, 1000 + i)).ToArray())));
        store.EnqueuePaged(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            Enumerable.Range(0, 1000).Select(i =>
                TestDevices.Snapshot(device.Id, device.Points.First().Id, i)).ToArray())));

        var vm = new HistoryViewModel(cache, store, new UiDispatcher(),
            NullLogger<HistoryViewModel>.Instance);
        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        vm.SelectedPoint = new PointOption(device.Points.First().Id, "P1", "40001");

        await vm.QueryCommand.ExecuteAsync(null);
        Assert.Equal(1000, vm.Rows.Count);
        Assert.Equal(1, vm.PageNumber);
        Assert.True(vm.CanGoNext);
        Assert.False(vm.CanGoPrev);

        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal(2, vm.PageNumber);
        Assert.False(vm.CanGoNext);
        Assert.True(vm.CanGoPrev);

        await vm.PrevPageCommand.ExecuteAsync(null);
        Assert.Equal(1000, vm.Rows.Count);
        Assert.Equal(1, vm.PageNumber);
        Assert.False(vm.CanGoPrev);

        Assert.Equal([(1000, 0), (1000, 1000), (1000, 0)], store.PagedCalls);
    }

    [Fact]
    public async Task QueryAsync_rejects_missing_dates_without_touching_store()
    {
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device);
        cache.EnqueueSuccess(device);

        var store = new StagedMeasurementStore();
        var vm = new HistoryViewModel(cache, store, new UiDispatcher(),
            NullLogger<HistoryViewModel>.Instance);
        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        vm.SelectedPoint = new PointOption(device.Points.First().Id, "P1", "40001");

        // DatePicker 清空后回写 DateTime? 为 null（ADR-027 P3-6），查询应给出提示而非沿用旧日期
        vm.FromDate = null;
        vm.ToDate = null;
        await vm.QueryCommand.ExecuteAsync(null);

        Assert.Equal("请选择起止日期", vm.StatusText);
        Assert.Empty(vm.Rows);
        Assert.Empty(store.PagedCalls);
    }
}
