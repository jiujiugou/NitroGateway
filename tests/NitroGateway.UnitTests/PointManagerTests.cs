using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 点位管理器单元测试——重点是失败路径：仓库保存/删除失败时必须向上传播，
/// 不能静默返回成功（否则前端会误认为点位已保存）。
/// </summary>
public class PointManagerTests
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly FakePointRepository _repo = new();
    private readonly FakeDeviceSnapshotCache _cache = new();
    private readonly PointManager _manager;

    public PointManagerTests()
    {
        _manager = new PointManager(_repo, _cache, NullLogger<PointManager>.Instance);
    }

    /// <summary>正常新增点位：成功返回点位并落库。</summary>
    [Fact]
    public async Task AddAsync_Success_ReturnsPoint()
    {
        var point = MakePoint("Temp1");
        var result = await _manager.AddAsync(_deviceId, point);
        Assert.True(result.IsSuccess);
        Assert.Equal("Temp1", result.Value!.Name);
        Assert.True(_repo.Points.ContainsKey(point.Id));
        Assert.Equal(1, _cache.InvalidateCount);
    }

    /// <summary>仓库保存失败时 AddAsync 必须返回失败，不能假装成功。</summary>
    [Fact]
    public async Task AddAsync_RepositoryFailure_ReturnsFailure()
    {
        _repo.FailSaves = true;
        var result = await _manager.AddAsync(_deviceId, MakePoint("Temp1"));
        Assert.True(result.IsFailure);
        Assert.Contains("磁盘满", result.Error!.Message);
        Assert.Equal(0, _cache.InvalidateCount);
    }

    /// <summary>仓库保存失败时 UpdateAsync 必须返回失败。</summary>
    [Fact]
    public async Task UpdateAsync_RepositoryFailure_ReturnsFailure()
    {
        _repo.FailSaves = true;
        var result = await _manager.UpdateAsync(_deviceId, MakePoint("Temp1"));
        Assert.True(result.IsFailure);
    }

    /// <summary>仓库删除失败时 RemoveAsync 必须返回失败。</summary>
    [Fact]
    public async Task RemoveAsync_RepositoryFailure_ReturnsFailure()
    {
        _repo.FailDeletes = true;
        var result = await _manager.RemoveAsync(_deviceId, Guid.NewGuid());
        Assert.True(result.IsFailure);
    }

    /// <summary>批量导入部分失败时必须上报失败点名称，不能静默丢弃。</summary>
    [Fact]
    public async Task ImportAsync_PartialFailure_ReturnsFailureWithPointNames()
    {
        _repo.FailOnName = "BadPoint";
        var result = await _manager.ImportAsync(
            _deviceId,
            new[] { MakePoint("GoodPoint"), MakePoint("BadPoint") });

        Assert.True(result.IsFailure);
        Assert.Contains("BadPoint", result.Error!.Message);
    }

    /// <summary>ADR-005 P2-1：批量导入走批量路径，不逐条保存。</summary>
    [Fact]
    public async Task ImportAsync_BatchSuccess_UsesBatchPath()
    {
        var result = await _manager.ImportAsync(
            _deviceId,
            new[] { MakePoint("A"), MakePoint("B") });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _repo.BatchSaveCalls);
        Assert.Equal(0, _repo.SaveCalls);
        Assert.Equal(2, _repo.Points.Count);
        Assert.Equal(1, _cache.InvalidateCount);
    }

    /// <summary>ADR-002 P2-2：批量导入部分失败（逐条回退成功）也应失效缓存。</summary>
    [Fact]
    public async Task ImportAsync_PartialFailure_InvalidatesCache()
    {
        _repo.FailOnName = "BadPoint";
        var result = await _manager.ImportAsync(
            _deviceId,
            new[] { MakePoint("GoodPoint"), MakePoint("BadPoint") });

        Assert.True(result.IsFailure);
        Assert.True(_cache.InvalidateCount >= 1);
    }

    private static DevicePoint MakePoint(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = "40001",
        DataType = DataType.Float
    };

    private sealed class FakeDeviceSnapshotCache : IDeviceSnapshotCache
    {
        public int InvalidateCount { get; private set; }
        public void Invalidate() => InvalidateCount++;
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => throw new NotSupportedException("PointManager 不调用 GetAllAsync");
    }

    /// <summary>FakePointRepository：内存字典模拟 SQLite 点位持久化，可注入保存/删除失败。</summary>
    private sealed class FakePointRepository : IPointRepository
    {
        public readonly Dictionary<Guid, DevicePoint> Points = new();

        public int BatchSaveCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public bool FailSaves { get; set; }

        public bool FailDeletes { get; set; }

        public string? FailOnName { get; set; }

        public Task<OperationResult> SaveAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        {
            SaveCalls++;
            if (FailSaves || (FailOnName is not null && point.Name == FailOnName))
                return Task.FromResult(OperationResult.Failure(OperationalError.Storage("磁盘满")));
            Points[point.Id] = point;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> SaveBatchAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
        {
            BatchSaveCalls++;
            if (FailSaves || (FailOnName is not null && points.Any(p => p.Name == FailOnName)))
                return Task.FromResult(OperationResult.Failure(OperationalError.Storage("磁盘满")));
            foreach (var point in points)
                Points[point.Id] = point;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        {
            if (FailDeletes)
                return Task.FromResult(OperationResult.Failure(OperationalError.Storage("磁盘满")));
            Points.Remove(pointId);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(
            Guid deviceId, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<DevicePoint>>.Success(
                Points.Values.ToList()));
    }
}
