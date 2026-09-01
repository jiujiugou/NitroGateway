using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Security;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// OPC UA 节点浏览 API（ADR-070 层次 1 P0-1）：供前端点位配置「从树选点」。
/// 与 <see cref="WriteService"/> 同范式：驱动池取长连接、未连接先建连、用后不断连；
/// 浏览失败/超时不置 Faulted（不污染采集状态机）。仅 Admin/Operator 可用（浏览是配置工具）。
/// </summary>
[ApiController, Route("api/devices")]
[Authorize(Roles = Roles.AdminOperator)]
public sealed class OpcUaBrowseController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IProtocolDriverPool _pool;

    public OpcUaBrowseController(IDeviceManager devices, IProtocolDriverPool pool)
    {
        _devices = devices;
        _pool = pool;
    }

    /// <summary>
    /// 浏览设备 OPC UA 节点树某层子节点。parent 缺省 = Objects 目录（根）。
    /// 设备不存在 → 404；协议不支持浏览 → 400；连接/浏览失败 → 400（Message 携带原因）。
    /// </summary>
    [HttpGet("{deviceId:guid}/browse")]
    public async Task<ActionResult<ApiResponse<List<BrowseNodeDto>>>> Browse(
        Guid deviceId, [FromQuery] string? parent, CancellationToken ct)
    {
        var deviceResult = await _devices.GetAsync(deviceId, ct);
        if (deviceResult.IsFailure)
            return NotFound(ApiResponse<List<BrowseNodeDto>>.Fail("NotFound", "设备不存在"));

        var driver = _pool.GetOrCreate(deviceResult.Value!);
        // 能力声明优先：非 OPC UA（Modbus/S7 等）不建连即拒绝，避免为浏览空连一次。
        if (!driver.Capability.SupportsBrowse || driver is not IBrowseableDriver browseable)
            return BadRequest(ApiResponse<List<BrowseNodeDto>>.Fail("Browse", "协议不支持节点浏览"));

        if (driver.State != DriverState.Connected)
        {
            var connect = await driver.ConnectAsync(ct);
            if (connect.IsFailure)
                return BadRequest(ApiResponse<List<BrowseNodeDto>>.Fail("Browse", $"设备连接失败：{connect.Error!.Message}"));
        }

        var result = await browseable.BrowseAsync(parent ?? "", ct);
        if (result.IsFailure)
            return BadRequest(ApiResponse<List<BrowseNodeDto>>.Fail("Browse", result.Error!.Message));

        return Ok(ApiResponse<List<BrowseNodeDto>>.Ok(result.Value!.Select(Map).ToList()));
    }

    private static BrowseNodeDto Map(BrowseNode n) => new()
    {
        NodeId = n.NodeId,
        Name = n.Name,
        TypeName = n.TypeName,
        IsVariable = n.IsVariable,
        Access = n.Access
    };
}
