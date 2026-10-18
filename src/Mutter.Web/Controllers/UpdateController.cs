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
        var staged = _applier.GetStaged();
        if (staged is not null)
        {
            check.Info.Staged = true;
            check.Info.StagedVersion = staged.Version;
        }
        return Ok(check.Info);
    }

    /// <summary>下载、校验并暂存最新正式版；不写安装标记，等待用户确认后才安装。</summary>
    [HttpPost("stage")]
    public async Task<ActionResult<UpdateStageResultDto>> Stage()
    {
        if (!_applier.TryBegin())
            return Conflict(new { error = "已有更新正在进行" });

        try
        {
            var check = await _updateService.GetStatusAsync(CancellationToken.None);
            if (check.Info.Error is not null)
                return BadRequest(new { error = check.Info.Error });
            if (!check.Info.HasUpdate)
                return BadRequest(new { error = "当前已是最新版本" });
            if (!check.Info.CanSelfUpdate || check.Plan is null)
                return BadRequest(new { error = check.Info.SelfUpdateReason ?? "当前环境不支持一键更新" });

            var staged = await _applier.StageAsync(check.Plan, AppContext.BaseDirectory, CancellationToken.None);
            return Ok(new UpdateStageResultDto
            {
                Version = staged.Version,
                AssetName = staged.AssetName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载更新包失败");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = ex.Message });
        }
        finally
        {
            _applier.End();
        }
    }

    /// <summary>用户确认后安装已暂存的更新，并请求宿主重启；Windows 由 helper 完成替换。</summary>
    [HttpPost("install")]
    public ActionResult<UpdateApplyResultDto> Install()
    {
        var staged = _applier.GetStaged();
        if (staged is null)
            return BadRequest(new { error = "更新包尚未下载，请先下载更新" });

        if (!_applier.TryBegin())
            return Conflict(new { error = "已有更新正在进行" });

        var restarting = false;
        try
        {
            var appDir = AppContext.BaseDirectory;
            _applier.ConfirmStaged(appDir, staged);
            if (OperatingSystem.IsWindows())
                _applier.PrepareWindowsHelper(appDir, staged);

            restarting = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                AppLifecycle.RequestRestart();
            });

            return Ok(new UpdateApplyResultDto
            {
                Version = staged.Version,
                Restarting = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装更新失败");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = ex.Message });
        }
        finally
        {
            // 成功安装后保持忙碌直到进程退出，避免重启窗口内重复触发。
            if (!restarting)
                _applier.End();
        }
    }
}
