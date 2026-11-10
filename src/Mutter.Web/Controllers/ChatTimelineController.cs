using Microsoft.AspNetCore.Mvc;
using Mutter.Web.Models;
using Mutter.Web.Services;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatTimelineController(ChatTimelineReader reader) : ControllerBase
{
    [HttpGet("timeline")]
    public async Task<ActionResult<ChatTimelinePageDto>> Timeline(string? groupId = null, string? source = null,
        string? q = null, int page = 1, int pageSize = 50, string? snapshot = null, CancellationToken ct = default)
    {
        if (q?.Length > 200) return BadRequest(new { error = "查找内容最多 200 字" });
        if (!ChatTimelineReader.Sources.Contains(source ?? "")) return BadRequest(new { error = "无效的消息来源" });
        if (!ChatTimelineReader.TrySnapshot(snapshot, out _, out _)) return BadRequest(new { error = "无效的历史分页快照，请刷新重试" });
        return Ok(await reader.ReadAsync(groupId, source, q, Math.Max(1, page), Math.Clamp(pageSize, 1, 100), snapshot, ct));
    }

    [HttpGet("timeline/decisions/{id:long}")]
    public async Task<ActionResult<ChatDecisionDto>> Decision(long id, CancellationToken ct)
    {
        var detail = await reader.DecisionAsync(id, ct);
        return detail is null ? NotFound(new { error = "这条决策留痕已不存在，聊天正文仍可查看" }) : Ok(detail);
    }
}
