using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 设备持久化实现</summary>
public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly NitroGatewayDbContext _db;

    public SqliteDeviceRepository(NitroGatewayDbContext db) => _db = db;

    public async Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default)
    {
        var existing = await _db.Devices.FindAsync([device.Id], ct);
        if (existing is null)
        {
            _db.Devices.Add(DomainMapper.ToEntity(device));
        }
        else
        {
            var updated = DomainMapper.ToEntity(device);
            _db.Entry(existing).CurrentValues.SetValues(updated);
        }
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        var entity = await _db.Devices.FindAsync([deviceId], ct);
        if (entity is null) return OperationResult.Success();
        _db.Devices.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default)
    {
        var entity = await _db.Devices
            .Include(d => d.Points)
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct);

        if (entity is null)
            return OperationalError.General("设备不存在");
        
        var device = DomainMapper.ToDomain(entity);
        foreach (var pe in entity.Points)
            device.AddPoint(DomainMapper.ToDomain(pe));

        return device;
    }

    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await _db.Devices
            .Include(d => d.Points)
            .ToListAsync(ct);

        return entities.Select(e =>
        {
            var d = DomainMapper.ToDomain(e);
            foreach (var pe in e.Points) d.AddPoint(DomainMapper.ToDomain(pe));
            return d;
        }).ToList();
    }

    public async Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(
        DeviceStatus status, CancellationToken ct = default)
    {
        var statusStr = status.ToString();
        var entities = await _db.Devices
            .Include(d => d.Points)
            .Where(d => d.Status == statusStr)
            .ToListAsync(ct);

        return entities.Select(e =>
        {
            var d = DomainMapper.ToDomain(e);
            foreach (var pe in e.Points) d.AddPoint(DomainMapper.ToDomain(pe));
            return d;
        }).ToList();
    }
}
