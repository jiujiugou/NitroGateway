using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 站点目录 API（ADR-035 第 1 步 Web 维度 + ADR-036 中心站点管理）。
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

    /// <summary>获取站点详情列表（ADR-036）：含显示名、来源指纹、首见/最近时间与冲突标记。</summary>
    [HttpGet("info")]
    public async Task<ActionResult<ApiResponse<List<SiteInfo>>>> GetSiteInfos()
    {
        var r = await _catalog.GetSiteInfosAsync();
        return r.IsSuccess
            ? Ok(ApiResponse<List<SiteInfo>>.Ok(r.Value!.ToList()))
            : BadRequest(ApiResponse<List<SiteInfo>>.Fail("Sites", r.Error!.Message));
    }

    /// <summary>改名/绑定站点显示名（ADR-036）；Admin/Operator 可写。</summary>
    [HttpPut("{siteId}/rename")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<object>>> Rename(string siteId, [FromBody] RenameSiteRequest? req)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            return BadRequest(ApiResponse<object>.Fail("Rename", "siteId 不能为空"));

        var name = req?.DisplayName?.Trim() ?? "";
        if (name.Length > 100)
            return BadRequest(ApiResponse<object>.Fail("Rename", "显示名不能超过 100 字符"));

        var r = await _catalog.RenameSiteAsync(siteId, name);
        return r.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { }))
            : BadRequest(ApiResponse<object>.Fail("Rename", r.Error!.Message));
    }
}
