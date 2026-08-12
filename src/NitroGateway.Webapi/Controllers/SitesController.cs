using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 站点目录 API（ADR-035 第 1 步 Web 维度）。
/// 返回中心库中实际出现过数据的 siteId 列表，供前端站点下拉；设备/点位是共享配置不归属站点。
/// </summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class SitesController : ControllerBase
{
    private readonly ISiteCatalog _catalog;

    public SitesController(ISiteCatalog catalog) => _catalog = catalog;

    /// <summary>获取全部已标注站点（去重排序）；未标注数据归"全部站点"。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<string>>>> GetSites()
    {
        var r = await _catalog.GetSitesAsync();
        return r.IsSuccess
            ? Ok(ApiResponse<List<string>>.Ok(r.Value!.ToList()))
            : BadRequest(ApiResponse<List<string>>.Fail("Sites", r.Error!.Message));
    }
}
