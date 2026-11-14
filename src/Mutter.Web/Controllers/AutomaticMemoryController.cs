using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Models.Config;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/groups/{groupId}/memory-auto")]
public sealed class AutomaticMemoryController(AppDbContext db, AutomaticMemoryRepository memories, TimeProvider clock) : ControllerBase
{
    private static DateTime? Utc(DateTime? time) => time.HasValue ? DateTime.SpecifyKind(time.Value, DateTimeKind.Utc) : null;
    [HttpGet]
    public async Task<IActionResult> Get(string groupId, CancellationToken ct)
    {
        var state = await memories.StateAsync(groupId, ct);
        var config = AutomaticMemoryConfig.Decode(state?.ConfigJson);
        var group = await db.GroupConfigs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupId == groupId, ct);
        var pending = state is null ? 0 : await db.ChatMessages.AsNoTracking().Where(m => m.ChatType == 0
                && m.GroupId == groupId && !m.IsBotMessage && m.Id > state.LastProcessedRowId
                && (group != null && group.TriggerMode == 2 || m.HasAtBot))
            .Take(1000).CountAsync(ct);
        return Ok(new
        {
            Config = config, Revision = state?.Revision ?? 0,
            Limits = new
            {
                Capacity = AutomaticMemoryRepository.Capacity,
                PerGroupDailyCalls = AutomaticMemoryRepository.PerGroupDailyCalls,
                GlobalDailyCalls = AutomaticMemoryRepository.GlobalDailyCalls,
                MaxCount = 1000,
                MaxIntervalMinutes = 10080,
                MaxSchedules = 4
            },
            Status = new
            {
                Pending = pending, PendingCapped = pending == 1000,
                LastOutcome = state?.LastOutcome == "running" && state.NextAttemptAt <= clock.GetUtcNow().UtcDateTime
                    ? "interrupted" : state?.LastOutcome ?? "not_enabled",
                LastCompletedAt = Utc(state?.LastCompletedAt),
                state?.LastProcessedRowId, NextAttemptAt = Utc(state?.NextAttemptAt),
                CallsToday = state?.QuotaDay == AutomaticMemoryRepository.Day(clock.GetUtcNow().UtcDateTime) ? state.CallsToday : 0,
                GroupEnabled = group?.IsEnabled == true
            },
            Items = (await memories.ItemsAsync(groupId, ct)).Select(i => new
            {
                i.Id, i.Content, i.SubjectUserId, Tags = AutomaticMemoryRepository.Read(i.TagsJson, new List<string>()),
                FirstSeenAt = Utc(i.FirstSeenAt), LastSeenAt = Utc(i.LastSeenAt), ExpiresAt = Utc(i.ExpiresAt)
            }),
            Runs = AutomaticMemoryRepository.Read(state?.RecentRunsJson ?? "[]", new List<AutomaticMemoryRepository.Run>())
        });
    }

    public sealed class Settings
    {
        public AutomaticMemoryConfig Config { get; set; } = new();
        public long Revision { get; set; }
    }
    [HttpPut]
    public async Task<IActionResult> Save(string groupId, Settings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId) || groupId.Length > 256) return BadRequest(new { error = "群 ID 无效" });
        try { await memories.SaveConfigAsync(groupId, settings.Config ?? new(), settings.Revision, ct); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "群记忆设置或处理状态已更新，请刷新后重试" }); }
        return await Get(groupId, ct);
    }
    [HttpDelete]
    public async Task<IActionResult> Clear(string groupId, CancellationToken ct)
    {
        await memories.ClearAsync(groupId, ct);
        return Ok(new { message = "自动记忆已清空，后续只学习新消息" });
    }
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(string groupId, long id, CancellationToken ct) =>
        await memories.DeleteAsync(groupId, id, ct)
            ? Ok(new { message = "记忆已删除" })
            : NotFound(new { error = "记忆不存在或已被删除" });

    [HttpGet("{id:long}/sources")]
    public async Task<IActionResult> Sources(string groupId, long id, CancellationToken ct)
    {
        var item = await db.GroupMemoryItems.AsNoTracking().SingleOrDefaultAsync(i => i.GroupId == groupId && i.Id == id, ct);
        if (item is null) return NotFound(new { error = "记忆不存在或已被删除" });
        var ids = AutomaticMemoryRepository.Read(item.SourceRowIdsJson, new List<long>()).Take(3).ToList();
        return Ok(await db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.Id) && m.ChatType == 0
            && m.GroupId == groupId && !m.IsBotMessage).OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.UserName, m.PlainText, CreatedAt = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc) }).ToListAsync(ct));
    }
}
