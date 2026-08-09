using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 设备持久化实现（EF Core + DomainMapper）。
/// 由 DI 以 Scoped 生命周期注册（见 <see cref="SqliteServiceCollectionExtensions"/>），
/// 与 DbContext 同生命周期，天然适配 Web 请求内的事务与跟踪。
/// 所有操作异常统一经 <see cref="SqliteErrorClassifier"/> 归类为 OperationResult（ADR-018 P2-2），
/// 与 Alarm 仓储/测量存储的"异常不抛出"契约一致，使上层 manager 的 IsFailure 分支真实可达。
/// </summary>
public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly NitroGatewayDbContext _db;

    /// <summary>注入 EF 上下文；依赖 DI 保证上下文生命周期不超出仓储</summary>
    public SqliteDeviceRepository(NitroGatewayDbContext db) => _db = db;

    /// <summary>
    /// 保存或更新设备：按 Id 查重，存在则用领域值覆盖当前实体（upsert）。
    /// 保存失败（含约束违反）归类为 OperationResult 返回，不抛出。
    /// </summary>
    public async Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            // ADR-018 P2-2：EF/Sqlite 异常（约束违反、锁定等）归类返回，不冒泡成 500
            return SqliteErrorClassifier.Classify(ex, "设备保存失败");
        }
    }

    /// <summary>
    /// 删除指定设备；设备不存在时视为成功（幂等删除）。
    /// 级联删除其全部点位（DeleteBehavior.Cascade）。异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> DeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            var entity = await _db.Devices.FindAsync([deviceId], ct);
            if (entity is null) return OperationResult.Success();
            _db.Devices.Remove(entity);
            await _db.SaveChangesAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            // ADR-018 P2-2：删除异常归类返回，使 DeviceManager.UnregisterAsync 的失败分支可达
            return SqliteErrorClassifier.Classify(ex, "设备删除失败");
        }
    }

    /// <summary>
    /// 按 ID 查询设备并附带全部点位；不存在时返回 Failure（General），与接口文档一致。
    /// 查询异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            // ADR-018 P2-2：查询异常归类返回
            return SqliteErrorClassifier.Classify(ex, "设备查询失败");
        }
    }

    /// <summary>获取全部设备（含点位），用于缓存预热、管理面板列表等场景。异常归类返回，不抛出。</summary>
    public async Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            // ADR-018 P2-2：查询异常归类返回
            return SqliteErrorClassifier.Classify(ex, "设备查询失败");
        }
    }

    /// <summary>
    /// 按通信状态筛选设备（含点位）；状态以枚举字符串等值匹配存储列。
    /// 供设备健康监控按状态快照/告警使用。异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(
        DeviceStatus status, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            // ADR-018 P2-2：查询异常归类返回
            return SqliteErrorClassifier.Classify(ex, "设备查询失败");
        }
    }
}
