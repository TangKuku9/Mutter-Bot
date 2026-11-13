using Microsoft.AspNetCore.Mvc;
using Mutter.Core.Services;

namespace Mutter.Web.Controllers;

/// <summary>
/// 全局出站网络设置。默认直连并在建连时校验目标地址；
/// 开启系统代理后由代理解析目标，地址策略只能覆盖代理本身，因此仅作为显式选项提供。
/// </summary>
[ApiController]
[Route("api/config/network")]
public sealed class NetworkController(OutboundSettingsStore settings) : ControllerBase
{
    public sealed record NetworkSettingsDto(bool AllowSystemProxy);

    [HttpGet]
    public ActionResult<NetworkSettingsDto> Get() => Ok(new NetworkSettingsDto(settings.AllowSystemProxy));

    [HttpPut]
    public IActionResult Save([FromBody] NetworkSettingsDto? dto)
    {
        if (dto is null) return BadRequest(new { error = "缺少网络设置" });
        if (!settings.TrySave(dto.AllowSystemProxy, out var error))
            return BadRequest(new { error });
        return NoContent();
    }
}
