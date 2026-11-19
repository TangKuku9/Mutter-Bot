using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Core.Services.WebSearch;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Services;
using NameStatus = Mutter.Infrastructure.Services.GroupStickerNamingService.NameStatus;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/groups/{groupId}/stickers")]
public sealed class GroupStickersController(IGroupStickerLibrary library, AppDbContext db, RichUiCapability richUi,
    GroupStickerNamingService naming, IConfigRepository configs)
    : ControllerBase
{
    private const int MaxUploadRequestBytes = StickerSettings.MaxSourceBytes + 1024 * 1024;

    public sealed record SettingsRequest(bool AutoCollectStickers, bool UseStickersInReplies, bool UseStickersInProactive,
        int StickerReplyTendency, int StickerProactiveTendency, int StickerIntervalMinutes, bool? AutoNameStickers = null,
        string? StickerSource = null);

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(string groupId, CancellationToken ct)
    {
        if (!await IsKnownAsync(groupId, ct)) return NotFound();
        var group = await db.GroupConfigs.AsNoTracking().SingleOrDefaultAsync(row => row.GroupId == groupId, ct);
        return Ok(await SettingsDtoAsync(groupId, group ?? new GroupConfigEntity(), ct));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(string groupId, [FromBody] SettingsRequest request, CancellationToken ct)
    {
        if (!await IsKnownAsync(groupId, ct)) return NotFound();
        var group = await db.GroupConfigs.FindAsync([groupId], ct);
        if (group is null)
        {
            group = new GroupConfigEntity { GroupId = groupId };
            db.GroupConfigs.Add(group);
        }
        group.AutoCollectStickers = request.AutoCollectStickers;
        if (request.AutoNameStickers is { } autoName) group.AutoNameStickers = autoName;
        group.UseStickersInReplies = request.UseStickersInReplies;
        group.UseStickersInProactive = request.UseStickersInProactive;
        if (request.StickerSource is { } source) group.StickerSource = StickerSettings.NormalizeSource(source);
        group.StickerReplyTendency = StickerSettings.NormalizeTendency(request.StickerReplyTendency);
        group.StickerProactiveTendency = StickerSettings.NormalizeTendency(request.StickerProactiveTendency);
        group.StickerIntervalMinutes = StickerSettings.NormalizeInterval(request.StickerIntervalMinutes);
        await db.SaveChangesAsync(ct);
        return Ok(await SettingsDtoAsync(groupId, group, ct));
    }

    [HttpGet]
    public async Task<IActionResult> List(string groupId, CancellationToken ct) =>
        Ok(await library.ListAsync(groupId, ct));

    public sealed record CopyRequest(string? TargetGroupId, IReadOnlyList<string>? Hashes);

    [HttpPost("copy")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Copy(string groupId, [FromBody] CopyRequest request, CancellationToken ct)
    {
        var result = await library.CopyAsync(groupId, request.TargetGroupId, request.Hashes, ct);
        return result.Status switch
        {
            StickerCopyStatus.Success => Ok(new { result.Copied, result.AlreadyInTarget }),
            StickerCopyStatus.UnknownGroup => NotFound(new { error = "来源群或目标群不存在，请刷新群列表" }),
            StickerCopyStatus.StickerUnavailable => NotFound(new { error = "来源群有表情已移除或文件不可用，请调整选择" }),
            StickerCopyStatus.TargetFull => Conflict(new { error = $"目标群图库还可添加 {result.Available} 张，请减少所选表情" }),
            _ => BadRequest(new { error = "请选择其他群和有效的表情" })
        };
    }

    [HttpPost]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = StickerSettings.MaxSourceBytes)]
    public async Task<IActionResult> Import(string groupId, [FromForm] IFormFile? file, [FromForm] string? label,
        CancellationToken ct)
    {
        if (file is null || file.Length <= 0 || file.Length > StickerSettings.MaxSourceBytes)
            return BadRequest(new { error = "请选择不超过 16 MiB 的静态图片" });
        await using var input = file.OpenReadStream();
        var bytes = new byte[(int)file.Length];
        await input.ReadExactlyAsync(bytes, ct);
        var error = await library.ImportAsync(groupId, label, bytes, ct);
        return error is null ? NoContent() : BadRequest(new { error });
    }

    [HttpPost("{hash}/ai-name")]
    public async Task<IActionResult> SuggestName(string groupId, string hash, CancellationToken ct)
    {
        var result = await naming.SuggestAsync(groupId, hash, ct);
        return result.Status switch
        {
            NameStatus.Success => Ok(new { result.Label }),
            NameStatus.NotFound => NotFound(new { error = "未找到本群的表情图片" }),
            NameStatus.NotConfigured => Conflict(new { error = "请先在 AI 设置中保存支持图片输入的模型" }),
            NameStatus.InputBudgetTooSmall => Conflict(new { error = "当前 AI 的输入预算不足以附带图片，请在 AI 设置中调整" }),
            NameStatus.Busy => StatusCode(StatusCodes.Status429TooManyRequests, new { error = "AI 正在为其他表情取名，请稍后重试" }),
            NameStatus.Timeout => StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "AI 取名超时，请稍后重试" }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = "AI 未能生成名称，请检查模型是否支持图片输入后重试" })
        };
    }

    [HttpGet("{hash}/image")]
    public async Task<IActionResult> Image(string groupId, string hash, CancellationToken ct)
    {
        var image = await library.ReadAsync(groupId, hash, forReply: false, ct);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Bytes, image.ContentType);
    }

    public sealed record UpdateRequest(string? Label, bool IsEnabled, bool PreserveLabel = false);

    [HttpPut("{hash}")]
    public async Task<IActionResult> Update(string groupId, string hash, [FromBody] UpdateRequest request, CancellationToken ct) =>
        await library.UpdateAsync(groupId, hash, request.Label, request.IsEnabled, ct, request.PreserveLabel)
            ? NoContent() : NotFound();

    [HttpDelete("{hash}")]
    public async Task<IActionResult> Delete(string groupId, string hash, CancellationToken ct) =>
        await library.DeleteAsync(groupId, hash, ct) ? NoContent() : NotFound();

    private Task<bool> IsKnownAsync(string groupId, CancellationToken ct) =>
        db.KnownGroups.AnyAsync(row => row.GroupOpenId == groupId, ct);

    private async Task<object> SettingsDtoAsync(string groupId, GroupConfigEntity group, CancellationToken ct) => new
    {
        group.AutoCollectStickers, group.AutoNameStickers, group.UseStickersInReplies, group.UseStickersInProactive,
        group.StickerReplyTendency, group.StickerProactiveTendency, group.StickerIntervalMinutes,
        StickerSource = StickerSettings.NormalizeSource(group.StickerSource),
        OnlineSearchAvailable = ExternalSearchService.CanSearchImages(await configs.GetAiConfigAsync(ct)),
        group.IsEnabled, group.AiProactiveEnabled, MarkdownAvailable = richUi.UseMarkdown(groupId)
    };
}
