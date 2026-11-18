using Microsoft.AspNetCore.Mvc;
using Mutter.Core.Interfaces;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/groups/{groupId}/stickers")]
public sealed class GroupStickersController(IGroupStickerLibrary library) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string groupId, CancellationToken ct) =>
        Ok(await library.ListAsync(groupId, ct));

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
}
