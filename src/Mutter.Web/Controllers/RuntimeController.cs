using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController(IConversationWorkQueue queue, AppDbContext db, IAiImageResolver images) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var calls = await db.AiCallRecords.AsNoTracking().OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id).Take(100).ToListAsync(ct);
        var successful = calls.Where(c => c.State == "success").Select(c => c.ElapsedMs).Order().ToArray();
        var names = await db.KnownGroups.AsNoTracking().ToDictionaryAsync(g => g.GroupOpenId, g => g.Name, ct);
        var snapshot = queue.Snapshot();
        // 列表只需要状态摘要；步骤只随 /operations/{id} 返回，避免每 5 秒轮询重复搬运过程明细。
        var queueView = snapshot with
        {
            Tasks = snapshot.Tasks.OrderByDescending(t => t.State == "running" ? 2 : t.State == "queued" ? 1 : 0)
                .ThenByDescending(t => t.EnqueuedAt).Take(60)
                .Select(t => t with { Steps = Array.Empty<RuntimeStep>() }).ToArray()
        };
        return Ok(new
        {
            Queue = queueView, Calls = calls.Take(20), GroupNames = names, ImageCache = images.Snapshot(),
            SampleCount = calls.Count, FailureCount = calls.Count(c => c.State != "success"),
            AverageMs = successful.Length == 0 ? 0 : (long)successful.Average(),
            P95Ms = successful.Length == 0 ? 0 : successful[(int)Math.Ceiling(successful.Length * .95) - 1]
        });
    }

    [HttpGet("operations/{id}")]
    public async Task<IActionResult> GetOperation(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) return BadRequest(new { error = "无效的任务标识" });
        var calls = await db.AiCallRecords.AsNoTracking().Where(c => c.OperationId == id)
            .OrderBy(c => c.StartedAt).ThenBy(c => c.Id).Take(100).ToListAsync(ct);
        var deliveries = await db.OutgoingReplies.AsNoTracking().Where(r => r.AiOperationId == id)
            .OrderBy(r => r.CreatedAt).Select(r => new
            {
                r.Id, r.CreatedAt, r.CompletedAt, r.State, r.Channel, r.Detail,
                Content = r.Content.Length > 2000 ? r.Content.Substring(0, 2000) : r.Content
            }).Take(20).ToListAsync(ct);
        var task = queue.Snapshot().Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null && calls.Count == 0 && deliveries.Count == 0) return NotFound(new { error = "该任务已不在近期记录中" });
        return Ok(new { Task = task, Calls = calls, Deliveries = deliveries });
    }
}
