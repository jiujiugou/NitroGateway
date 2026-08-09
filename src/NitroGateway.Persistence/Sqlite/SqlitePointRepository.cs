using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 点位持久化实现（EF Core + DomainMapper）。
/// 由 DI 以 Scoped 生命周期注册；点位始终归属某一设备（DeviceId 外键），删除设备级联删除点位。
/// 所有操作异常统一经 <see cref="SqliteErrorClassifier"/> 归类为 OperationResult（ADR-018 P2-2）。
/// </summary>
public sealed class SqlitePointRepository : IPointRepository
{
    private readonly NitroGatewayDbContext _db;

    /// <summary>注入 EF 上下文；依赖 DI 保证上下文生命周期不超出仓储</summary>
    public SqlitePointRepository(NitroGatewayDbContext db) => _db = db;

    /// <summary>
    /// 保存或更新单个点位：按 Id 查重，存在则覆盖（upsert），并强制归属指定设备。
    /// 保存失败（含约束违反）归类为 OperationResult 返回，不抛出。
    /// </summary>
    public async Task<OperationResult> SaveAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            // ADR-018 P2-2：EF/Sqlite 异常归类返回，使 PointManager 的 IsFailure 分支可达
            return SqliteErrorClassifier.Classify(ex, "点位保存失败");
        }
    }

    /// <summary>
    /// ADR-005 P2-1：批量保存走单事务（EF Core SaveChanges 默认单事务），
    /// 一次性 upsert 全部点位，替代逐条 SaveAsync 的 N 次往返。
    /// 空列表直接成功返回，不做任何查询；已存在的行多次出现时以最后一次覆盖为准，
    /// 批次内重复且不存在的 Id 会在 SaveChanges 时因主键冲突失败（归类返回）。
    /// </summary>
    public async Task<OperationResult> SaveBatchAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0)
            return OperationResult.Success();

        try
        {
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
        catch (Exception ex)
        {
            // ADR-018 P2-2：批量保存异常归类返回
            return SqliteErrorClassifier.Classify(ex, "点位批量保存失败");
        }
    }

    /// <summary>
    /// 删除指定设备下的指定点位（双条件约束，防止误删其他设备的同名 ID）；
    /// 不存在时视为成功（幂等删除）。异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> DeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
    {
        try
        {
            var entity = await _db.Points.FirstOrDefaultAsync(p => p.Id == pointId && p.DeviceId == deviceId, ct);
            if (entity is null) return OperationResult.Success();
            _db.Points.Remove(entity);
            await _db.SaveChangesAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            // ADR-018 P2-2：删除异常归类返回
            return SqliteErrorClassifier.Classify(ex, "点位删除失败");
        }
    }

    /// <summary>获取指定设备下的全部点位（无排序保证，调用方按需排序）。异常归类返回，不抛出。</summary>
    public async Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(
        Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            var entities = await _db.Points
                .Where(p => p.DeviceId == deviceId)
                .ToListAsync(ct);

            return entities.Select(DomainMapper.ToDomain).ToList();
        }
        catch (Exception ex)
        {
            // ADR-018 P2-2：查询异常归类返回
            return SqliteErrorClassifier.Classify(ex, "点位查询失败");
        }
    }
}
