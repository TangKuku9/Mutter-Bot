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

    /// <summary>切换群或来源时的轻量首屏；不等待全量总数。</summary>
    [HttpGet("timeline/latest")]
    public async Task<ActionResult<ChatTimelinePageDto>> Latest(string? groupId = null, string? source = null,
        int pageSize = 50, string? q = null, CancellationToken ct = default)
    {
        if (!ChatTimelineReader.Sources.Contains(source ?? "")) return BadRequest(new { error = "无效的消息来源" });
        // 轻量首屏不做查找：静默忽略 q 会让调用方误以为结果已被过滤。
        if (!string.IsNullOrEmpty(q)) return BadRequest(new { error = "轻量首屏不支持查找，请使用精确查询" });
        return Ok(await reader.ReadLatestAsync(groupId, source, Math.Clamp(pageSize, 1, 100), ct));
    }

    /// <summary>首屏显示后单独补齐精确总数；使用同一快照，不受新消息挤动。</summary>
    [HttpGet("timeline/count")]
    public async Task<ActionResult<ChatTimelineCountDto>> Count(string? groupId = null, string? source = null,
        string? q = null, string? snapshot = null, CancellationToken ct = default)
    {
        if (q?.Length > 200) return BadRequest(new { error = "查找内容最多 200 字" });
        if (!ChatTimelineReader.Sources.Contains(source ?? "")) return BadRequest(new { error = "无效的消息来源" });
        if (!ChatTimelineReader.TrySnapshot(snapshot, out _, out _)) return BadRequest(new { error = "无效的历史分页快照，请刷新重试" });
        return Ok(await reader.CountAsync(groupId, source, q, snapshot, ct));
    }

    [HttpGet("timeline/decisions/{id:long}")]
    public async Task<ActionResult<ChatDecisionDto>> Decision(long id, CancellationToken ct)
    {
        var detail = await reader.DecisionAsync(id, ct);
        return detail is null ? NotFound(new { error = "这条决策留痕已不存在，聊天正文仍可查看" }) : Ok(detail);
    }
}
