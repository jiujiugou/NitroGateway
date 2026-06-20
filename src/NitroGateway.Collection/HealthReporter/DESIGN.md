# HealthReporter 设计文档 v1

## 定位

将本轮采集结果上报给 `IDeviceHealthMonitor`。
只负责"报"，不参与阈值判定。判定逻辑在 Monitor 内部。

---

## 接口

```csharp
public interface IHealthReporter
{
    /// <summary>上报一轮采集结果（每轮采集结束后调一次）</summary>
    void Report(Guid deviceId, int successCount, int failCount, string? errorMessage);
}
```

## 实现

```csharp
public sealed class HealthReporter : IHealthReporter
{
    private readonly IDeviceHealthMonitor _healthMonitor;

    public HealthReporter(IDeviceHealthMonitor healthMonitor)
    {
        _healthMonitor = healthMonitor;
    }

    public void Report(Guid deviceId, int successCount, int failCount, string? errorMessage)
    {
        try
        {
            if (failCount > 0)
                _healthMonitor.ReportFailure(deviceId, errorMessage ?? "采集失败");
            else
                _healthMonitor.ReportSuccess(deviceId);
        }
        catch (Exception ex)
        {
            // 健康上报失败不能崩采集循环
            System.Diagnostics.Debug.WriteLine($"HealthReporter 异常: {ex.Message}");
        }
    }
}
```

## 和 DeviceHealthMonitor 的分工

```
HealthReporter                  DeviceHealthMonitor
──────────────────────          ──────────────────────────
"这一轮 OK"                    SuccessCount++
                                SuccessCount ≥ 3 → 触发 Online
                                
"这一轮 2 个失败"               SuccessCount 清零
                                FailCount++
                                FailCount ≥ 10 → 触发 Offline
                                → IDeviceManager.UpdateStatusAsync()
```

## 调用方

`CollectionEngine` 在每轮采集结束后调一次，汇总整轮结果：

```csharp
// CollectionEngine.CollectDeviceAsync 内部
var snapshots = _pipeline.Process(deviceId, readResult);
var success = snapshots.Count(s => s.Quality == QualityCode.Good);
var fail = snapshots.Count - success;
_healthReporter.Report(deviceId, success, fail, readResult.IsFailure ? readResult.Error!.Message : null);
```

## 约束

| 约束 | 说明 |
|---|---|
| 每轮只报一次 | 不对单个点位报，整轮汇总 |
| 同步 | Report 同步返回，不阻塞采集循环 |
| 不抛异常 | 内部 catch 所有异常，健康上报崩不能带崩采集 |
| 不调 Alarm | 告警模块订阅 Device 领域事件，独立运行 |

---

## 演进

| v1 | 连续 N 次失败 → Offline | **当前** |
| v2 | 成功率窗口判定（最近 100 次 < 50% → Offline） | 偶发超时需要降噪 |
| v3 | 多病种分类（超时/CRC/非法地址分别计数） | 故障排查 |
| v4 | 预测性告警（响应时间趋势） | 提前发现退化 |
| v5 | 根因分析（总线故障 vs 设备故障） | 多设备同时故障 |
