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

    [HttpGet("progress")]
    public ActionResult<UpdateProgressDto> GetProgress() => Ok(_applier.GetProgress());

    /// <summary>
    /// 启动后台下载、校验与暂存；接口立即返回，前端通过 /progress 观察。
    /// 重复调用是幂等的：正在进行时返回当前状态，已暂存时直接返回暂存版本。
    /// </summary>
    [HttpPost("stage")]
    public async Task<ActionResult<UpdateStageResultDto>> Stage(CancellationToken ct)
    {
        var current = _applier.GetProgress();
        if (current.Active)
            return Accepted(ToStageResult(current));

        var check = await _updateService.GetStatusAsync(ct);
        if (check.Info.Error is not null)
            return BadRequest(new { error = check.Info.Error });
        if (!check.Info.HasUpdate)
            return BadRequest(new { error = "当前已是最新版本" });
        if (!check.Info.CanSelfUpdate || check.Plan is null)
            return BadRequest(new { error = check.Info.SelfUpdateReason ?? "当前环境不支持一键更新" });

        var start = _applier.StartStage(check.Plan);
        if (start.Error is not null)
            return Conflict(new { error = start.Error });

        var result = new UpdateStageResultDto
        {
            Version = start.StagedUpdate?.Version ?? check.Plan.Version,
            AssetName = start.StagedUpdate?.AssetName ?? check.Plan.AssetName,
            InProgress = !start.Staged,
            Staged = start.Staged
        };
        return start.Staged ? Ok(result) : Accepted(result);
    }

    private static UpdateStageResultDto ToStageResult(UpdateProgressDto progress) => new()
    {
        Version = progress.Version ?? string.Empty,
        AssetName = progress.AssetName ?? string.Empty,
        InProgress = progress.Active,
        Staged = progress.Staged
    };

    /// <summary>用户确认后安装已暂存的更新，并请求宿主重启；Windows 由 helper 完成替换。</summary>
    [HttpPost("install")]
    public ActionResult<UpdateApplyResultDto> Install()
    {
        var staged = _applier.GetStaged();
        if (staged is null)
            return BadRequest(new { error = "更新包尚未下载，请先下载更新" });

        if (!_applier.TryBeginInstall())
            return Conflict(new { error = _applier.GetProgress().Active ? "更新包仍在下载，请等待完成" : "已有更新正在进行" });

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
                _applier.EndInstall();
        }
    }
}
