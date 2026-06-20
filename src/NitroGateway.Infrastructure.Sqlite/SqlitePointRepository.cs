using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.Infrastructure.Sqlite;

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
