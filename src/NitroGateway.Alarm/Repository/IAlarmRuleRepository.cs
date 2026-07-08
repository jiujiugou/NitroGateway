using NitroGateway.Alarm.Domain;
using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

/// <summary>告警规则存储接口。实现放在 Persistence 层</summary>
public interface IAlarmRuleRepository
{
    /// <summary>获取某设备某点位的所有启用规则</summary>
    Task<OperationResult<IReadOnlyList<AlarmRule>>> GetByPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default);

    /// <summary>获取所有启用规则</summary>
    Task<OperationResult<IReadOnlyList<AlarmRule>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>保存规则（新增或更新）</summary>
    Task<OperationResult> SaveAsync(AlarmRule rule, CancellationToken ct = default);

    /// <summary>删除规则</summary>
    Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default);
}
