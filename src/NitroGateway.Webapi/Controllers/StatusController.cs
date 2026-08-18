using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi.Models;

using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class StatusController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly IForwardBuffer _buffer;
    private readonly IMqttClient _mqtt;
    private readonly ForwardingThrottle _throttle;
    private readonly ICircuitBreakerRegistry _breakers;
    private readonly string _siteId;

    public StatusController(
        IDeviceManager devices,
        IDeviceHealthMonitor healthMonitor,
        IForwardBuffer buffer,
        IMqttClient mqtt,
        ForwardingThrottle throttle,
        ICircuitBreakerRegistry breakers,
        IConfiguration configuration)
    {
        _devices = devices;
        _healthMonitor = healthMonitor;
        _buffer = buffer;
        _mqtt = mqtt;
        _throttle = throttle;
        _breakers = breakers;
        // ADR-054：web 收敛为纯边缘单一身份——本站点 ID 来自 Site:Id 配置（缺省 default），
        // 替代原「中心站点目录」概念；前端系统状态页与设备表单据此展示本网关站点。
        _siteId = SiteOptions.Resolve(configuration[SiteOptions.IdKey]);
    }

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
        var breakerStates = _breakers.GetAll().Select(kv => (object)new
        {
            DeviceId = kv.Key.ToString(),
            State = kv.Value.State.ToString(),
            IsOpen = kv.Value.State == CircuitState.Open
        }).ToList();

        var onlineResult = await _devices.GetByStatusAsync(Domain.Devices.DeviceStatus.Online);
        var onlineCount = onlineResult.IsSuccess ? onlineResult.Value!.Count : 0;

        // ADR-017 P3-3：客户端在计数查询完成前断开时返回 0 而非 500（GetCountAsync 取消时仍抛 OCE）
        int bufferBacklog;
        try
        {
            bufferBacklog = await _buffer.GetCountAsync(HttpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // 请求已中止，结果不再送达，直接按 0 收尾
            bufferBacklog = 0;
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            SiteId = _siteId,
            MqttState = _mqtt.State.ToString(),
            MqttConnected = _mqtt.State == MqttConnectionState.Connected,
            BufferBacklog = bufferBacklog,
            ThrottleBatchSize = _throttle.MaxBatchSize,
            ThrottleDelayMs = _throttle.DelayMs,
            OnlineDevices = onlineCount,
            CircuitBreakers = breakerStates
        }));
    }
}
