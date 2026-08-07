using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Evaluation;
using NitroGateway.Alarm.Notification;
using NitroGateway.Alarm.Repository;
using NitroGateway.Domain.Events;

namespace NitroGateway.Alarm.Hosted;

/// <summary>
/// 告警后台服务。实现 <see cref="IPointStoredSink"/> 接收采集数据，
/// 通过 <see cref="AlarmEvaluator"/> 评估规则，持久化告警并通知。
/// 使用 Channel 解耦，不阻塞采集流程。
/// </summary>
public sealed class AlarmHostedService : BackgroundService, IPointStoredSink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlarmHostedService> _logger;
    private readonly ConcurrentQueue<PointStoredEvent> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);

    public AlarmHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AlarmHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ════════════════════════════════════════════
    //  IPointStoredSink — Dispatcher 调用，不阻塞
    // ════════════════════════════════════════════

    /// <inheritdoc />
    public ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default)
    {
        _queue.Enqueue(e);
        try { _signal.Release(); } catch (SemaphoreFullException) { }
        return ValueTask.CompletedTask;
    }

    // ════════════════════════════════════════════
    //  BackgroundService — 消费队列
    // ════════════════════════════════════════════

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var evaluator = new AlarmEvaluator();

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            while (_queue.TryDequeue(out var e))
            {
                await ProcessEventAsync(evaluator, e, stoppingToken);
            }
        }
    }

    // ════════════════════════════════════════════
    //  核心处理
    // ════════════════════════════════════════════

    private async Task ProcessEventAsync(
        AlarmEvaluator evaluator,
        PointStoredEvent e,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ruleRepo = scope.ServiceProvider.GetRequiredService<IAlarmRuleRepository>();
            var alarmRepo = scope.ServiceProvider.GetRequiredService<IAlarmRepository>();
            var notifiers = scope.ServiceProvider.GetServices<IAlarmNotifier>();
            var now = DateTime.UtcNow;

            // ADR-002 P2-3：按设备一次取全部启用规则，内存按点位过滤，避免每点一次 DB 往返
            var rulesResult = await ruleRepo.GetByDeviceAsync(e.DeviceId, ct);
            if (rulesResult.IsFailure)
            {
                _logger.LogError("加载设备 {DeviceId} 告警规则失败: {Error}", e.DeviceId, rulesResult.Error!.Message);
                return;
            }
            var deviceRules = rulesResult.Value!;

            foreach (var snapshot in e.Snapshots)
            {
                if (snapshot.Value is not IConvertible) continue;

                double value;
                try { value = Convert.ToDouble(snapshot.Value); }
                catch { continue; }

                var rules = deviceRules.Where(r => r.PointId == snapshot.DevicePointId).ToList();
                if (rules.Count == 0) continue;

                var evaluations = evaluator.Evaluate(
                    e.DeviceId,
                    snapshot.DevicePointId,
                    value,
                    rules,
                    now);

                foreach (var eval in evaluations)
                {
                    await ApplyEvaluationAsync(alarmRepo, notifiers, eval, ct);
                }
            }
        }
        catch (Exception ex)
        {
            // ADR-002 P1-1：单事件容错——仓储/评估异常只记日志，不再击穿后台服务
            _logger.LogError(ex, "处理设备 {DeviceId} 存储事件异常", e.DeviceId);
        }
    }

    private async Task ApplyEvaluationAsync(
        IAlarmRepository alarmRepo,
        IEnumerable<IAlarmNotifier> notifiers,
        AlarmEvaluation eval,
        CancellationToken ct)
    {
        switch (eval.NewState)
        {
            case Domain.AlarmState.Active:
            {
                if (eval.Alarm is null) return;

                var saveResult = await alarmRepo.SaveAsync(eval.Alarm, ct);
                if (saveResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "告警触发: {RuleId} Severity={Severity} Value={Value}",
                        eval.RuleId, eval.Severity, eval.TriggerValue);

                    foreach (var notifier in notifiers)
                    {
                        try { await notifier.NotifyAsync(eval.Alarm, ct); }
                        catch (Exception ex) { _logger.LogError(ex, "通知失败 {Notifier}", notifier.Name); }
                    }
                }
                break;
            }

            case Domain.AlarmState.Resolved:
            {
                var updateResult = await alarmRepo.UpdateStateAsync(eval.ExistingAlarmId, Domain.AlarmState.Resolved, ct);
                if (updateResult.IsFailure)
                {
                    _logger.LogError("告警恢复持久化失败 {AlarmId}: {Error}", eval.ExistingAlarmId, updateResult.Error!.Message);
                }
                else
                {
                    _logger.LogInformation("告警恢复: {AlarmId}", eval.ExistingAlarmId);
                }
                break;
            }

            case Domain.AlarmState.Pending:
                _logger.LogDebug("告警计时中: Rule={RuleId} Value={Value}", eval.RuleId, eval.TriggerValue);
                break;
        }
    }
}
