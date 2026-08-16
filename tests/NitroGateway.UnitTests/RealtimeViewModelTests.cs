using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Shared;
using LiveChartsCore.Defaults;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-027 P1-1：RealtimeViewModel 异步加载竞态——快速切换设备/点位时，
/// 旧查询结果晚到必须被版本守卫丢弃，不能覆盖新选择。
/// ADR-045：原始缓冲 2h/7200 窗口 + 显示集合降采样（≤1000 点）+ 页面生命周期（IsActive）暂停/恢复。
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
        // ADR-047：store 查询经 Task.Run 移到线程池执行，等待第一次查询真正出队后再触发第二次，
        // 保证两次历史查询按调用顺序各取到一个 gate（否则出队顺序不确定，竞态断言不稳定）。
        await TestWait.UntilAsync(() => store.PagedDequeueCount >= 1);
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
    public async Task Frame_driven_device_switch_does_not_query_latest_and_grid_is_immediate()
    {
        // ADR-050：切设备不再每次扫全历史。先发一帧把各设备点位最新值灌进内存缓存
        // （模拟已在线采集一段时间的设备），随后 选设备→切走→切回：
        // 网格全程即时用「配置 + 帧内存」填充，LatestDequeueCount 保持 0（未触发 DB 最新值查询）。
        var cache = new StagedSnapshotCache();
        var deviceA = TestDevices.Device("A", TestDevices.Point("P1"), TestDevices.Point("P2"));
        var deviceB = TestDevices.Device("B", TestDevices.Point("Q1"));
        cache.EnqueueSuccess(deviceA, deviceB); // 构造时 LoadDevicesAsync
        cache.EnqueueSuccess(deviceA, deviceB); // LoadPointsAsync(A)
        cache.EnqueueSuccess(deviceA, deviceB); // LoadPointsAsync(B)
        cache.EnqueueSuccess(deviceA, deviceB); // LoadPointsAsync(A)（切回）

        var store = new StagedMeasurementStore();
        var vm = new RealtimeViewModel(cache, store, new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        var p1 = deviceA.Points.ElementAt(0);
        var p2 = deviceA.Points.ElementAt(1);
        var q1 = deviceB.Points.First();
        await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
        {
            DeviceId = deviceA.Id,
            Snapshots =
            [
                TestDevices.Snapshot(deviceA.Id, p1.Id, 10.0),
                TestDevices.Snapshot(deviceA.Id, p2.Id, 20.0),
                TestDevices.Snapshot(deviceB.Id, q1.Id, 30.0)
            ]
        });
        _bridge.Flush();

        vm.SelectedDevice = new DeviceOption(deviceA.Id, deviceA.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == deviceA.Points.Count);
        Assert.Equal(0, store.LatestDequeueCount); // 帧内存已覆盖，未触发 DB
        Assert.Equal("10", vm.Points.First(p => p.PointId == p1.Id).ValueText); // 帧值即时显示

        vm.SelectedDevice = new DeviceOption(deviceB.Id, deviceB.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == deviceB.Points.Count);
        Assert.Equal(0, store.LatestDequeueCount);
        Assert.Equal("30", vm.Points.First(p => p.PointId == q1.Id).ValueText);

        vm.SelectedDevice = new DeviceOption(deviceA.Id, deviceA.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == deviceA.Points.Count);
        Assert.Equal(0, store.LatestDequeueCount); // 全程零 DB 最新值查询
        Assert.Equal("20", vm.Points.First(p => p.PointId == p2.Id).ValueText);
    }

    [Fact]
    public async Task Missing_point_falls_back_to_db_and_does_not_override_frame_value()
    {
        // ADR-050：冷启动/离线点位（从未在帧中出现）才走一次 DB 兜底；
        // 兜底只填缺失点位——在线点位以帧值为准，不被 DB 旧值覆盖。
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"), TestDevices.Point("P2"));
        cache.EnqueueSuccess(device); // 构造时 LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)

        var store = new StagedMeasurementStore();
        var p1 = device.Points.ElementAt(0);
        var p2 = device.Points.ElementAt(1);
        // DB 兜底：P1 返回旧值 99（应被帧值 10 覆盖忽略）、P2 返回缺失值 20（应被填充）
        store.EnqueueLatest(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
        [
            TestDevices.Snapshot(device.Id, p1.Id, 99.0),
            TestDevices.Snapshot(device.Id, p2.Id, 20.0)
        ])));
        var vm = new RealtimeViewModel(cache, store, new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        // 只发 P1 的帧（P2 离线，从未在帧中出现）
        await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
        {
            DeviceId = device.Id,
            Snapshots = [TestDevices.Snapshot(device.Id, p1.Id, 10.0)]
        });
        _bridge.Flush();

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        // 阶段① 网格立即出现（P1 帧值 10、P2 暂无值），随后 DB 兜底在线程池填充 P2
        await TestWait.UntilAsync(() => vm.Points.Count == device.Points.Count);
        await TestWait.UntilAsync(() => store.LatestDequeueCount == 1);
        await TestWait.UntilAsync(() => vm.Points.First(p => p.PointId == p2.Id).ValueText == "20");

        Assert.Equal(1, store.LatestDequeueCount); // 仅触发一次兜底查询
        Assert.Equal("10", vm.Points.First(p => p.PointId == p1.Id).ValueText); // 帧值不被 DB 旧值覆盖
    }

    [Fact]
    public async Task Raw_buffer_keeps_at_most_two_hour_window_and_chart_is_downsampled()
    {
        // ADR-037 S9/S12 + ADR-045 P2：原始缓冲上限 7200（1s×2h），溢出批量裁剪；
        // 显示集合是降采样后的 ≤ChartWindowPoints 点，最新值仍在右边缘
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)
        var store = new StagedMeasurementStore();
        // ADR-047：历史预载经 Task.Run 在线程池完成；测试无 UI 线程时 _ui.Post 内联到各自调用线程，
        // 帧追加（测试线程）与历史回调的 _rawValues.Clear()（线程池）不串行化。预载 1 点让回调末尾
        // 的 RefreshChart 有可见落点（ChartValues.Count==1），等它落定再灌帧，避免晚到回调清空已累计缓冲。
        store.EnqueuePaged(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(device.Id, device.Points.First().Id, 5.0)])));
        var vm = new RealtimeViewModel(cache, store, new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        vm.SelectedPoint = vm.Points[0];
        // 历史回调末尾才 RefreshChart（ChartValues 置 1），等它落定再灌帧（与 IsActive_false 同因）
        await TestWait.UntilAsync(() => vm.ChartValues.Count == 1);
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

        // 事件经桥接异步送达 VM：等原始缓冲灌满到 2h/7200 再断言，避免满负载下晚到帧导致计数抖动
        await TestWait.UntilAsync(() => vm.RawValues.Count == 7200);
        Assert.Equal(7200, vm.RawValues.Count); // 原始窗口 2h/7200
        Assert.Equal(7299d, vm.RawValues[^1].Value!.Value); // 最新值在缓冲尾部

        vm.RefreshChart(); // 测试无消息泵，手动触发降采样刷新
        Assert.InRange(vm.ChartValues.Count, 1, RealtimeViewModel.ChartWindowPoints);
        Assert.Equal(7299d, vm.ChartValues[^1].Value!.Value); // 右边缘 = 最新值
    }

    [Fact]
    public async Task Re_activate_reloads_devices_and_shows_newly_added_device()
    {
        // ADR-048：设备页新增设备后切回实时页，IsActive 重新置 true 触发重载，
        // 新设备出现在下拉列表（原实现只在构造时 LoadDevicesAsync 一次，新设备缺失）
        var cache = new StagedSnapshotCache();
        var deviceA = TestDevices.Device("A");
        cache.EnqueueSuccess(deviceA); // 构造时 LoadDevicesAsync

        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);
        await TestWait.UntilAsync(() => vm.Devices.Count == 1);

        // 设备页新增 B（设备目录缓存已失效，重新激活时拿到最新目录）
        var deviceB = TestDevices.Device("B");
        cache.EnqueueSuccess(deviceA, deviceB); // 重新激活时 LoadDevicesAsync

        vm.IsActive = false;
        vm.IsActive = true;
        await TestWait.UntilAsync(() => vm.Devices.Count == 2);

        Assert.Contains(vm.Devices, o => o.Id == deviceA.Id); // 原有设备仍在
        var optionB = Assert.Single(vm.Devices.Where(o => o.Id == deviceB.Id));
        Assert.Equal("B", optionB.Name);
    }

    [Fact]
    public async Task Re_activate_keeps_selected_device_when_renamed()
    {
        // ADR-048：设备页重命名后切回实时页，下拉项更新为新名称，
        // 且选中设备按 Id 恢复（DeviceOption 是记录，重命名替换了新实例，需重指向）
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // 构造时 LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)

        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);
        await TestWait.UntilAsync(() => vm.Devices.Count == 1);
        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);

        // 设备页把 A 重命名为 A2（缓存失效）
        device.Name = "A2";
        cache.EnqueueSuccess(device); // 重新激活时 LoadDevicesAsync
        cache.EnqueueSuccess(device); // SelectedDevice 按 Id 恢复时 LoadPointsAsync

        vm.IsActive = false;
        vm.IsActive = true;
        await TestWait.UntilAsync(() => vm.Devices.Count == 1 && vm.Devices[0].Name == "A2");

        Assert.Equal(device.Id, vm.SelectedDevice?.Id); // 选中设备按 Id 保持
        Assert.Equal("A2", vm.SelectedDevice!.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1); // 重指向后点位重载成功
    }

    [Fact]
    public void DownsampleMinMax_preserves_spikes_and_edges_within_budget()
    {
        var baseTime = DateTime.UtcNow.AddHours(-2);
        var raw = new List<DateTimePoint>(7200);
        for (var i = 0; i < 7200; i++)
            raw.Add(new DateTimePoint(baseTime.AddSeconds(i), i == 3600 ? 1000.0 : i));

        var sampled = RealtimeViewModel.DownsampleMinMax(raw, RealtimeViewModel.ChartWindowPoints);

        Assert.InRange(sampled.Count, 1, RealtimeViewModel.ChartWindowPoints);
        Assert.Equal(raw[0].Value!.Value, sampled[0].Value!.Value); // 首点保留
        Assert.Equal(raw[^1].Value!.Value, sampled[^1].Value!.Value); // 末点（最新值）保留
        Assert.Contains(sampled, p => p.Value == 1000.0); // 尖峰不被隔点抽稀丢掉
    }

    [Fact]
    public async Task IsActive_false_skips_frames_and_detaches_chart_on_deactivate()
    {
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)
        var store = new StagedMeasurementStore();
        store.EnqueuePaged(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(device.Id, device.Points.First().Id, 5.0)]))); // 选中点位的历史预载
        var vm = new RealtimeViewModel(cache, store, new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        vm.SelectedPoint = vm.Points[0];
        // 历史回调末尾才 RefreshChart（ChartValues 置 1），等它完成再停用，
        // 否则回调尾段可能在线程池晚到，把停用置空的 _series.Values 重新指回空集合
        await TestWait.UntilAsync(() => vm.ChartValues.Count == 1);

        // 停用：清空原始缓冲并摘除图表数据（LiveCharts 无数据可持有）
        vm.IsActive = false;
        Assert.Empty(vm.RawValues);
        Assert.Empty(vm.ChartValues);
        Assert.Null(vm.Series[0].Values);

        // 停用期间帧不再追加
        var point = device.Points.First();
        await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
        {
            DeviceId = device.Id,
            Snapshots = [TestDevices.Snapshot(device.Id, point.Id, 9.0)]
        });
        _bridge.Flush();
        Assert.Empty(vm.RawValues);

        // 重新激活：重载最近历史
        cache.EnqueueSuccess(device); // 重新激活时 LoadDevicesAsync（ADR-048，队列末尾补一次目录结果）
        store.EnqueuePaged(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(device.Id, point.Id, 11.0)])));
        vm.IsActive = true;
        await TestWait.UntilAsync(() => vm.RawValues.Count == 1);
        Assert.Equal(11.0, vm.RawValues[0].Value!.Value);
    }

    [Fact]
    public async Task Frame_updates_cache_but_grid_refresh_is_throttled()
    {
        // ADR-051：表格节流——帧值先入内存缓存（O(1) 无通知），DataGrid 行按节流周期批量刷，
        // 不再逐帧 Update（原 500×4×5fps ≈ 1 万通知/秒压满 UI 线程饿死交互）。
        // 拉大节流周期，确定性断言「帧已到、缓存已更新、网格未逐帧刷」。
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)
        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);
        vm.GridRefreshInterval = TimeSpan.FromHours(1); // 测试不靠真实时间触发节流

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        var point = device.Points.First();

        await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
        {
            DeviceId = device.Id,
            Snapshots = [TestDevices.Snapshot(device.Id, point.Id, 42.0)]
        });
        _bridge.Flush();

        Assert.Equal(42d, (double)vm.LatestByPoint[point.Id].Value!); // 值已入内存缓存（后续刷新/切设备会用）
        Assert.Equal("—", vm.Points[0].ValueText); // 网格未被逐帧刷新（节流生效）
    }

    [Fact]
    public async Task Grid_refreshes_from_cache_on_throttle_boundary_and_resume()
    {
        // ADR-051：到节流周期后由内存缓存批量补齐网格；窗口恢复（IsActive 重新置 true）走同一补齐路径。
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)
        cache.EnqueueSuccess(device); // 恢复激活时 LoadDevicesAsync（ADR-048）
        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);
        vm.GridRefreshInterval = TimeSpan.FromHours(1); // 节流不靠真实时间

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        var point = device.Points.First();

        await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
        {
            DeviceId = device.Id,
            Snapshots = [TestDevices.Snapshot(device.Id, point.Id, 42.0)]
        });
        _bridge.Flush();
        Assert.Equal("—", vm.Points[0].ValueText); // 节流期未逐帧刷表

        vm.RefreshGridFromCache(); // 节流到点 / 恢复补齐的批量路径
        Assert.Equal("42", vm.Points[0].ValueText);

        // 失焦（停用）期间帧被丢弃；恢复激活时 OnIsActiveChanged 用缓存一次补齐表格
        vm.IsActive = false;
        vm.IsActive = true;
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        Assert.Equal("42", vm.Points[0].ValueText);
    }

    [Fact]
    public async Task Grid_refreshes_each_frame_when_throttle_disabled()
    {
        // ADR-051 控制组：节流间隔为 0（测试专用）时每帧都刷——证明批量路径与逐帧路径
        // 结果一致，节流只降刷新频率、不改变刷新正确性。
        var cache = new StagedSnapshotCache();
        var device = TestDevices.Device("A", TestDevices.Point("P1"));
        cache.EnqueueSuccess(device); // LoadDevicesAsync
        cache.EnqueueSuccess(device); // LoadPointsAsync(A)
        var vm = new RealtimeViewModel(cache, new StagedMeasurementStore(), new UiDispatcher(),
            _bridge, NullLogger<RealtimeViewModel>.Instance);
        vm.GridRefreshInterval = TimeSpan.Zero; // 每帧都刷（无节流）

        vm.SelectedDevice = new DeviceOption(device.Id, device.Name);
        await TestWait.UntilAsync(() => vm.Points.Count == 1);
        var point = device.Points.First();

        await ((IPointStoredSink)_bridge).OnStoredAsync(new PointStoredEvent
        {
            DeviceId = device.Id,
            Snapshots = [TestDevices.Snapshot(device.Id, point.Id, 7.0)]
        });
        _bridge.Flush();

        Assert.Equal("7", vm.Points[0].ValueText); // 无节流时帧到即刷
    }
}
