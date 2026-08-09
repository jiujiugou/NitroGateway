using NitroGateway.Shared;

namespace NitroGateway.Alarm.Repository;

/// <summary>告警记录存储接口。实现放在 Persistence 层</summary>
public interface IAlarmRepository
{
    /// <summary>保存新告警</summary>
    Task<OperationResult> SaveAsync(Domain.Alarm alarm, CancellationToken ct = default);

    /// <summary>更新告警状态</summary>
    Task<OperationResult> UpdateStateAsync(Guid alarmId, Domain.AlarmState state, CancellationToken ct = default);

    /// <summary>查询设备当前活跃告警</summary>
    Task<OperationResult<IReadOnlyList<Domain.Alarm>>> GetActiveByDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>查询所有活跃告警</summary>
    Task<OperationResult<IReadOnlyList<Domain.Alarm>>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>按时间范围查询历史告警（倒序；limit 夹紧 1..1000，默认 1000）</summary>
    Task<OperationResult<IReadOnlyList<Domain.Alarm>>> QueryAsync(DateTime from, DateTime to, int limit = 1000, CancellationToken ct = default);
}
