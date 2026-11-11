using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/proactive")]
public sealed class ProactiveParametersController(AppDbContext db, IConfigRepository configs) : ControllerBase
{
    public sealed class GroupParametersDto
    {
        public bool Enabled { get; set; }
        public ProactiveOverrides Overrides { get; set; } = new();
    }

    public sealed class PresetWriteDto
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ProactiveOverrides Values { get; set; } = new();
    }

    public sealed record PresetDto(string Id, string Name, string Description, bool IsBuiltIn,
        ProactiveOverrides Values, DateTime? UpdatedAt = null);

    [HttpGet("groups/{groupId}/config")]
    public async Task<IActionResult> GetGroup(string groupId, CancellationToken ct)
    {
        var id = groupId.Trim();
        var known = await db.KnownGroups.AsNoTracking().SingleOrDefaultAsync(g => g.GroupOpenId == id, ct);
        var group = await configs.GetGroupConfigAsync(id, ct);
        if (known is null && group is null) return NotFound(new { error = "未找到这个群，请刷新群列表" });
        var global = await configs.GetProactiveConfigAsync(ct);
        return Ok(new
        {
            GroupId = id, Name = known?.Name, IsEnabled = group?.IsEnabled ?? false,
            Enabled = group?.AiProactiveEnabled ?? false,
            Overrides = group?.ProactiveOverrides ?? new ProactiveOverrides(),
            EffectiveConfig = global.WithOverrides(group?.ProactiveOverrides),
            MemoryAllowed = group?.MemoryInjectionEnabled != false
        });
    }

    [HttpPut("groups/{groupId}/config")]
    public async Task<IActionResult> SaveGroup(string groupId, [FromBody] GroupParametersDto dto, CancellationToken ct)
    {
        var id = groupId.Trim();
        var entity = await db.GroupConfigs.SingleOrDefaultAsync(g => g.GroupId == id, ct);
        if (entity is null)
        {
            if (!await db.KnownGroups.AnyAsync(g => g.GroupOpenId == id, ct))
                return NotFound(new { error = "未找到这个群，请刷新群列表" });
            entity = new GroupConfigEntity { GroupId = id };
            db.GroupConfigs.Add(entity);
        }
        var values = dto.Overrides ?? new ProactiveOverrides();
        if (!values.HasCompleteQuietHours()) return BadRequest(new { error = "静默开始与结束时间需要一起设置或一起恢复全局" });
        await NormalizeAsync(values, ct);
        entity.AiProactiveEnabled = dto.Enabled;
        entity.ProactiveOverridesJson = ProactiveOverridesCodec.Encode(values);
        await db.SaveChangesAsync(ct);
        return await GetGroup(id, ct);
    }

    [HttpGet("presets")]
    public async Task<IActionResult> GetPresets(CancellationToken ct)
    {
        var saved = await db.ProactivePresets.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id).ToListAsync(ct);
        return Ok(BuiltIns().Concat(saved.Select(ToDto)));
    }

    [HttpPost("presets")]
    public async Task<IActionResult> CreatePreset([FromBody] PresetWriteDto dto, CancellationToken ct)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        var description = dto.Description?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 24 || description.Length > 120)
            return BadRequest(new { error = "预设名称需为 1–24 字，说明最多 120 字" });
        var values = dto.Values ?? new ProactiveOverrides();
        if (!values.HasValues()) return BadRequest(new { error = "预设至少需要包含一项参数" });
        if (!values.HasCompleteQuietHours()) return BadRequest(new { error = "静默开始与结束时间需要一起保存" });
        if (BuiltIns().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new { error = "这个预设名称已存在，请换一个名称" });
        await NormalizeAsync(values, ct);
        var entity = new ProactivePresetEntity
        {
            Name = name, Description = description, ValuesJson = ProactiveOverridesCodec.Encode(values), UpdatedAt = DateTime.UtcNow
        };
        db.ProactivePresets.Add(entity);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            return Conflict(new { error = "这个预设名称已存在，请换一个名称" });
        }
        return Ok(ToDto(entity));
    }

    [HttpDelete("presets/{id:int}")]
    public async Task<IActionResult> DeletePreset(int id, CancellationToken ct)
    {
        var entity = await db.ProactivePresets.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return NotFound(new { error = "这条预设已不存在" });
        db.ProactivePresets.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Ok(new { message = "预设已删除，已保存的群设置保持原样" });
    }

    private async Task NormalizeAsync(ProactiveOverrides values, CancellationToken ct)
    {
        values.Normalize();
        if (values.EvalMaxTokens.HasValue)
        {
            var contextWindow = (await configs.GetAiConfigAsync(ct))?.ContextWindowTokens ?? AiRequestBudget.DefaultContextWindow;
            values.EvalMaxTokens = AiRequestBudget.ClampOutputTokens(values.EvalMaxTokens.Value, contextWindow);
        }
    }

    private static PresetDto ToDto(ProactivePresetEntity entity) => new(entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        entity.Name, entity.Description, false, ProactiveOverridesCodec.Decode(entity.ValuesJson), entity.UpdatedAt);

    private static IEnumerable<PresetDto> BuiltIns() =>
    [
        new("quiet", "安静陪伴", "少打扰，等值得接的话题。", true, new()
        { Liveliness = 0, CooldownMinutes = 120, MaxPerGroupPerDay = 2, WindowSeconds = 120, WindowMaxMessages = 8, QuietStartHour = 22, QuietEndHour = 9 }),
        new("daily", "日常接话", "保持自然的参与节奏。", true, new()
        { Liveliness = 1, CooldownMinutes = 45, MaxPerGroupPerDay = 3, WindowSeconds = 60, WindowMaxMessages = 12, QuietStartHour = 23, QuietEndHour = 8 }),
        new("active", "活跃讨论", "更及时地参与正在进行的讨论。", true, new()
        { Liveliness = 2, CooldownMinutes = 10, MaxPerGroupPerDay = 12, WindowSeconds = 30, WindowMaxMessages = 18, QuietStartHour = 0, QuietEndHour = 0 })
    ];
}
