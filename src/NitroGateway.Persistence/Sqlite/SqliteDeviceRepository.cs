using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence.Security;
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
    /// <summary>OPC UA 连接参数中的密码键（PascalCase，与设备参数字典约定一致，ADR-073 D1）</summary>
    internal const string PasswordKey = "Password";

    private readonly NitroGatewayDbContext _db;
    private readonly ICredentialProtector _protector;

    /// <summary>
    /// 注入 EF 上下文与凭据保护器。凭据保护器在写库前加密 OPC UA Password、读库后解密（ADR-073 D5），
    /// 使 SQLite <c>ConnectionParams</c> 只存密文而域内/驱动路径为内存明文；依赖 DI 保证上下文生命周期不超出仓储。
    /// </summary>
    public SqliteDeviceRepository(NitroGatewayDbContext db, ICredentialProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    /// <summary>
    /// 保存或更新设备：按 Id 查重，存在则用领域值覆盖当前实体（upsert）。
    /// 保存失败（含约束违反）归类为 OperationResult 返回，不抛出。
    /// </summary>
    public async Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default)
    {
        try
        {
            // ADR-033 阶段 3/4：常规保存自动盖章（同步合并路径显式带时间戳，不走此处）
            if (device.UpdatedAt == default)
                device.UpdatedAt = DateTime.UtcNow;

            var existing = await _db.Devices.FindAsync([device.Id], ct);
            if (existing is null)
            {
                _db.Devices.Add(DomainMapper.ToEntity(device, p => Protect(device, p)));
            }
            else
            {
                var updated = DomainMapper.ToEntity(device, p => Protect(device, p));
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
            Unprotect(device);
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
                Unprotect(d);
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
                Unprotect(d);
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
    /// 写库前加密变换（ADR-073 D5）：仅 OPC UA 设备、且存在非空 Password 时，将其替换为保护器密文。
    /// 返回新字典，不改动入参域对象的明文参数（调用方后续 Map/outbox 仍按明文走剔除逻辑）。
    /// 非 OPC UA / 无密码参数原样返回（Modbus/S7 协议参数互不污染，ADR-073 D1）。
    /// </summary>
    private Dictionary<string, object> Protect(Device device, Dictionary<string, object> parameters)
    {
        if (!IsOpcUa(device.Protocol.Name)
            || !TryGetParamString(parameters, PasswordKey, out var password)
            || string.IsNullOrEmpty(password))
            return parameters;
        var copy = new Dictionary<string, object>(parameters, StringComparer.Ordinal);
        copy[PasswordKey] = _protector.Protect(password);
        return copy;
    }

    /// <summary>
    /// 读库后解密（ADR-073 D5）：OPC UA 设备且存在本保护器格式密文时还原为内存明文供驱动使用。
    /// 密钥缺失/错误在 <see cref="ICredentialProtector.Unprotect"/> 抛出（fail-fast，禁止明文回写兜底），
    /// 由方法外层 try 归类为 OperationResult 返回。
    /// </summary>
    private void Unprotect(Device device)
    {
        if (!IsOpcUa(device.Protocol.Name))
            return;
        var parameters = device.Connection.Parameters;
        if (!TryGetParamString(parameters, PasswordKey, out var stored) || stored.Length == 0)
            return;
        parameters[PasswordKey] = _protector.Unprotect(stored);
    }

    private static bool IsOpcUa(string protocolName)
        => string.Equals(protocolName, "OPC UA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从连接参数字典读取字符串值：兼容内存 string 与 SQLite JSON 反序列化的
    /// <see cref="JsonElement"/>（DomainMapper.DeserializeParams 产出的字典值为 JsonElement），
    /// 与 OpcUaSecurityParameters 读参口径一致（ADR-073 D1）。键缺失 → false。
    /// </summary>
    private static bool TryGetParamString(Dictionary<string, object> parameters, string key, out string value)
    {
        if (parameters.TryGetValue(key, out var raw))
        {
            switch (raw)
            {
                case string s:
                    value = s;
                    return true;
                case JsonElement { ValueKind: JsonValueKind.String } element:
                    value = element.GetString() ?? "";
                    return true;
            }
        }
        value = "";
        return false;
    }
}
