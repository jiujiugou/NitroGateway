using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Services;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 站点身份管理（ADR-036）：查看 / 修改 / 重新生成本站点唯一标识。
/// Web 收敛为纯边缘网关后为单站点，接口与桌面设置页「站点标识」区语义对齐：
/// 读操作所有角色可见，写操作（修改/重新生成）仅 Admin。
/// </summary>
[ApiController, Route("api/[controller]")]
public class SiteController : ControllerBase
{
    private readonly ISiteIdProvider _siteId;
    private readonly ILogger<SiteController> _logger;

    /// <param name="siteId">站点标识提供者（解析/保存/重新生成，app_meta 持久化）</param>
    /// <param name="logger">审计日志</param>
    public SiteController(ISiteIdProvider siteId, ILogger<SiteController> logger)
    {
        _siteId = siteId;
        _logger = logger;
    }

    /// <summary>查看当前站点身份（生效标识 + 来源 + 是否配置锁定）</summary>
    [HttpGet]
    [Authorize(Roles = Roles.AllRoles)]
    public ActionResult<ApiResponse<SiteIdentityDto>> Get()
        => Ok(ApiResponse<SiteIdentityDto>.Ok(ToDto(restartRequired: false)));

    /// <summary>
    /// 修改站点标识（先校验格式，持久化到 app_meta）。
    /// 配置/环境变量 Site:Id 锁定时此修改只影响本地库，重启后配置仍优先；改后需重启全面生效。
    /// </summary>
    [HttpPut]
    [Authorize(Roles = Roles.Admin)]
    public ActionResult<ApiResponse<SiteIdentityDto>> Update(SiteIdentityUpdateRequest request)
    {
        var result = _siteId.Save(request.SiteId ?? "");
        if (result.IsFailure)
            return BadRequest(ApiResponse<SiteIdentityDto>.Fail("SiteId", result.Error!.Message));

        _logger.LogInformation("站点身份已修改为 {SiteId}（重启后全面生效）", _siteId.Current);
        return Ok(ApiResponse<SiteIdentityDto>.Ok(ToDto(restartRequired: true)));
    }

    /// <summary>
    /// 重新生成站点标识（加密随机，概率唯一，持久化到 app_meta）。
    /// 生成后旧标识即失效：已落库设备的 siteId 归属不变，但上行 topic 与同步归属性切换需重启全面生效。
    /// </summary>
    [HttpPost("regenerate")]
    [Authorize(Roles = Roles.Admin)]
    public ActionResult<ApiResponse<SiteIdentityDto>> Regenerate()
    {
        var value = _siteId.Regenerate();
        _logger.LogInformation("站点身份已重新生成 {SiteId}（重启后全面生效）", value);
        return Ok(ApiResponse<SiteIdentityDto>.Ok(ToDto(restartRequired: true)));
    }

    /// <summary>组装站点身份视图</summary>
    /// <param name="restartRequired">GET=false（当前生效态）；修改/重新生成=true（提示重启生效）</param>
    private SiteIdentityDto ToDto(bool restartRequired) => new()
    {
        SiteId = _siteId.Current,
        Source = _siteId.Source.ToString(),
        ConfigPinned = _siteId.Source == SiteIdSource.Configured,
        RestartRequired = restartRequired
    };
}
