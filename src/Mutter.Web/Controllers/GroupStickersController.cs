using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/groups/{groupId}/stickers")]
public sealed class GroupStickersController(IGroupStickerLibrary library, AppDbContext db, RichUiCapability richUi)
    : ControllerBase
{
    private const int MaxUploadBytes = 960 * 1024;

    public sealed record SettingsRequest(bool AutoCollectStickers, bool UseStickersInReplies, bool UseStickersInProactive,
        int StickerReplyTendency, int StickerProactiveTendency, int StickerIntervalMinutes);

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(string groupId, CancellationToken ct)
    {
        if (!await IsKnownAsync(groupId, ct)) return NotFound();
        var group = await db.GroupConfigs.AsNoTracking().SingleOrDefaultAsync(row => row.GroupId == groupId, ct);
        return Ok(SettingsDto(groupId, group ?? new GroupConfigEntity()));
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
        group.UseStickersInReplies = request.UseStickersInReplies;
        group.UseStickersInProactive = request.UseStickersInProactive;
        group.StickerReplyTendency = StickerSettings.NormalizeTendency(request.StickerReplyTendency);
        group.StickerProactiveTendency = StickerSettings.NormalizeTendency(request.StickerProactiveTendency);
        group.StickerIntervalMinutes = StickerSettings.NormalizeInterval(request.StickerIntervalMinutes);
        await db.SaveChangesAsync(ct);
        return Ok(SettingsDto(groupId, group));
    }

    [HttpGet]
    public async Task<IActionResult> List(string groupId, CancellationToken ct) =>
        Ok(await library.ListAsync(groupId, ct));

    [HttpPost]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Import(string groupId, [FromForm] IFormFile? file, [FromForm] string? label,
        CancellationToken ct)
    {
        if (file is null || file.Length <= 0 || file.Length > MaxUploadBytes)
            return BadRequest(new { error = "请选择不超过 960 KB 的静态图片" });
        await using var input = file.OpenReadStream();
        var bytes = new byte[(int)file.Length];
        await input.ReadExactlyAsync(bytes, ct);
        var error = await library.ImportAsync(groupId, label, bytes, ct);
        return error is null ? NoContent() : BadRequest(new { error });
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

    public sealed record UpdateRequest(string? Label, bool IsEnabled);

    [HttpPut("{hash}")]
    public async Task<IActionResult> Update(string groupId, string hash, [FromBody] UpdateRequest request, CancellationToken ct) =>
        await library.UpdateAsync(groupId, hash, request.Label, request.IsEnabled, ct) ? NoContent() : NotFound();

    [HttpDelete("{hash}")]
    public async Task<IActionResult> Delete(string groupId, string hash, CancellationToken ct) =>
        await library.DeleteAsync(groupId, hash, ct) ? NoContent() : NotFound();

    private Task<bool> IsKnownAsync(string groupId, CancellationToken ct) =>
        db.KnownGroups.AnyAsync(row => row.GroupOpenId == groupId, ct);

    private object SettingsDto(string groupId, GroupConfigEntity group) => new
    {
        group.AutoCollectStickers, group.UseStickersInReplies, group.UseStickersInProactive,
        group.StickerReplyTendency, group.StickerProactiveTendency, group.StickerIntervalMinutes,
        group.IsEnabled, group.AiProactiveEnabled, MarkdownAvailable = richUi.UseMarkdown(groupId)
    };
}
