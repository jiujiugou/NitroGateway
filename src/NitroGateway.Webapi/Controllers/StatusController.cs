using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Deployment;

using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class StatusController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly IForwardBuffer? _buffer;
    private readonly IMqttClient? _mqtt;
    private readonly ForwardingThrottle? _throttle;
    private readonly ICircuitBreakerRegistry? _breakers;
    private readonly DeploymentMode _deploymentMode;

    public StatusController(
        IDeviceManager devices,
        IDeviceHealthMonitor healthMonitor,
        // ADR-044：采集/转发/MQTT 依赖在 Center 模式未注册，改可空注入；
        // Center 下返回 mode + 中心侧信息，采集侧字段为空/0（不再 DI 500）。
        IForwardBuffer? buffer,
        IMqttClient? mqtt,
        ForwardingThrottle? throttle,
        ICircuitBreakerRegistry? breakers,
        DeploymentMode deploymentMode)
    {
        _devices = devices;
        _healthMonitor = healthMonitor;
        _buffer = buffer;
        _mqtt = mqtt;
        _throttle = throttle;
        _breakers = breakers;
        _deploymentMode = deploymentMode;
    }

    /// <summary>
    /// 部署形态信息（ADR-044）：前端启动时取一次，按 Gateway/Center 裁剪 UI 能力。
    /// 结构稳定，B 阶段中心「意图下发/回执」等中心侧信息在此追加。
    /// </summary>
    [HttpGet("info")]
    public ActionResult<ApiResponse<object>> Info() =>
        Ok(ApiResponse<object>.Ok(new { mode = _deploymentMode.ToString() }));

    /// <summary>设备状态概览</summary>
    [HttpGet("devices")]
    public async Task<ActionResult<ApiResponse<List<DeviceStatusSummaryDto>>>> DeviceSummary()
    {
        var r = await _devices.GetAllAsync();
        if (r.IsFailure)
            return BadRequest(ApiResponse<List<DeviceStatusSummaryDto>>.Fail("Devices", r.Error!.Message));

        var summaries = r.Value!.Select(d => new DeviceStatusSummaryDto
        {
            DeviceId = d.Id.ToString(),
            DeviceName = d.Name,
            // ADR-002 P2-2（方案 1）：状态以 HealthMonitor 实时快照为准，配置缓存 Status 仅兜底
            Status = (_healthMonitor.GetSnapshot(d.Id)?.Status ?? d.Status).ToString()
        }).ToList();

        return Ok(ApiResponse<List<DeviceStatusSummaryDto>>.Ok(summaries));
    }

    /// <summary>设备健康详情</summary>
    [HttpGet("devices/health")]
    public ActionResult<ApiResponse<List<DeviceHealthDto>>> DeviceHealth()
    {
        var snapshots = _healthMonitor.GetAllSnapshots();
        var items = snapshots.Select(s => new DeviceHealthDto
        {
            DeviceId = s.DeviceId.ToString(),
            Status = s.Status.ToString(),
            LastCollectionAt = s.LastCollectionAt?.ToString("O"),
            ConsecutiveFailures = s.ConsecutiveFailures,
            ConsecutiveSuccesses = s.ConsecutiveSuccesses,
            LastError = s.LastError
        }).ToList();

        return Ok(ApiResponse<List<DeviceHealthDto>>.Ok(items));
    }

    /// <summary>系统状态面板（完整聚合）。异步等待设备状态查询，避免 .Result 阻塞线程（ADR-001 P2-10）</summary>
    [HttpGet("system")]
    public async Task<ActionResult<ApiResponse<object>>> SystemStatus()
    {
        // ADR-015: 诊断路径只读 State（纯查询），不调用 TryEnterProbe——
        // 读会抢占 HalfOpen 探测名额并饿死自愈探测；"是否熔断"由 State == Open 推导。
        var breakerStates = _breakers is null
            ? Enumerable.Empty<object>()
            : _breakers.GetAll().Select(kv => (object)new
            {
                DeviceId = kv.Key.ToString(),
                State = kv.Value.State.ToString(),
                IsOpen = kv.Value.State == CircuitState.Open
            }).ToList();

        var onlineResult = await _devices.GetByStatusAsync(Domain.Devices.DeviceStatus.Online);
        var onlineCount = onlineResult.IsSuccess ? onlineResult.Value!.Count : 0;

        // ADR-017 P3-3：客户端在计数查询完成前断开时返回 0 而非 500（GetCountAsync 取消时仍抛 OCE）
        var bufferBacklog = 0;
        if (_buffer is not null)
        {
            try
            {
                bufferBacklog = await _buffer.GetCountAsync(HttpContext.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // 请求已中止，结果不再送达，直接按 0 收尾
            }
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            Mode = _deploymentMode.ToString(),
            MqttState = _mqtt?.State.ToString() ?? "Unavailable",
            MqttConnected = _mqtt?.State == MqttConnectionState.Connected,
            BufferBacklog = bufferBacklog,
            ThrottleBatchSize = _throttle?.MaxBatchSize ?? 0,
            ThrottleDelayMs = _throttle?.DelayMs ?? 0,
            OnlineDevices = onlineCount,
            CircuitBreakers = breakerStates
        }));
    }
}
