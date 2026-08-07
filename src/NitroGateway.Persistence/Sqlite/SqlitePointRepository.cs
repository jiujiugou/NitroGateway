using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 点位持久化实现</summary>
public sealed class SqlitePointRepository : IPointRepository
{
    private readonly NitroGatewayDbContext _db;

    public SqlitePointRepository(NitroGatewayDbContext db) => _db = db;

    public async Task<OperationResult> SaveAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        var existing = await _db.Points.FindAsync([point.Id], ct);
        if (existing is null)
        {
            _db.Points.Add(DomainMapper.ToEntity(point, deviceId));
        }
        else
        {
            var updated = DomainMapper.ToEntity(point, deviceId);
            _db.Entry(existing).CurrentValues.SetValues(updated);
        }
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    /// <summary>
    /// ADR-005 P2-1：批量保存走单事务（EF Core SaveChanges 默认单事务），
    /// 一次性 upsert 全部点位，替代逐条 SaveAsync 的 N 次往返。
    /// </summary>
    public async Task<OperationResult> SaveBatchAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0)
            return OperationResult.Success();

        var ids = points.Select(p => p.Id).ToList();
        var existing = await _db.Points
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var point in points)
        {
            var entity = DomainMapper.ToEntity(point, deviceId);
            if (existing.TryGetValue(point.Id, out var current))
                _db.Entry(current).CurrentValues.SetValues(entity);
            else
                _db.Points.Add(entity);
        }

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        var entity = await _db.Points.FirstOrDefaultAsync(p => p.Id == pointId && p.DeviceId == deviceId, ct);
        if (entity is null) return OperationResult.Success();
        _db.Points.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
    {
        var entities = await _db.Points
            .Where(p => p.DeviceId == deviceId)
            .ToListAsync(ct);

        return entities.Select(DomainMapper.ToDomain).ToList();
    }
}
