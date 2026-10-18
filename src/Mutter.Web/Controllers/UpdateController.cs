using Microsoft.AspNetCore.Mvc;
using Mutter.Core.Services;
using Mutter.Web.Models;
using Mutter.Web.Services;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UpdateController : ControllerBase
{
    private readonly UpdateService _updateService;
    private readonly UpdateApplier _applier;
    private readonly ILogger<UpdateController> _logger;

    public UpdateController(
        UpdateService updateService,
        UpdateApplier applier,
        ILogger<UpdateController> logger)
    {
        _updateService = updateService;
        _applier = applier;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<UpdateInfoDto>> GetStatus(CancellationToken ct)
    {
        var check = await _updateService.GetStatusAsync(ct);
        return Ok(check.Info);
    }

    /// <summary>下载、校验并暂存最新正式版，随后请求宿主重启；Windows 由 helper 完成替换。</summary>
    [HttpPost("apply")]
    public async Task<ActionResult<UpdateApplyResultDto>> Apply()
    {
        if (!_applier.TryBegin())
            return Conflict(new { error = "已有更新正在进行" });

        var restarting = false;
        try
        {
            var check = await _updateService.GetStatusAsync(CancellationToken.None);
            if (check.Info.Error is not null)
                return BadRequest(new { error = check.Info.Error });
            if (!check.Info.HasUpdate)
                return BadRequest(new { error = "当前已是最新版本" });
            if (!check.Info.CanSelfUpdate || check.Plan is null)
                return BadRequest(new { error = check.Info.SelfUpdateReason ?? "当前环境不支持一键更新" });

            var appDir = AppContext.BaseDirectory;
            var staged = await _applier.StageAsync(check.Plan, appDir, CancellationToken.None);
            if (OperatingSystem.IsWindows())
                _applier.PrepareWindowsHelper(check.Plan, appDir, staged);

            restarting = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                AppLifecycle.RequestRestart();
            });

            return Ok(new UpdateApplyResultDto
            {
                Version = check.Plan.Version,
                Restarting = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用更新失败");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = ex.Message });
        }
        finally
        {
            // 成功暂存后保持忙碌直到进程退出，避免重启窗口内重复触发第二次更新。
            if (!restarting)
                _applier.End();
        }
    }
}
