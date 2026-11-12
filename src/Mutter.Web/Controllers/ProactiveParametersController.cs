using System.Text;
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
public sealed class ProactiveParametersController(AppDbContext db, IConfigRepository configs, TimeProvider? clock = null) : ControllerBase
{
    /// <summary>与 AI 配置预设一致的数量上限。</summary>
    private const int MaxProactivePresets = 50;

    /// <summary>覆盖 JSON 的体积上限，防止异常客户端塞入巨型载荷。</summary>
    private const int MaxOverridesJsonBytes = 16 * 1024;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// 部分更新契约：缺省字段不覆盖已有值（省略 enabled/overrides 均表示保持原样）。
    /// </summary>
    public sealed class GroupParametersDto
    {
        public bool? Enabled { get; set; }
        public ProactiveOverrides? Overrides { get; set; }
    }

    public sealed class PresetWriteDto
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>更新接口缺省 values 表示保留原参数。</summary>
        public ProactiveOverrides? Values { get; set; }
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
        var overrides = group?.ProactiveOverrides ?? new ProactiveOverrides();
        // 与 MessageRouter 的“无配置 @ 默认放行”一致：已知群没有配置行时按参与响应展示，不显示成停用。
        var isEnabled = group?.IsEnabled ?? true;
        // MemoryAllowed 只反映「群管理是否允许」，供面板区分提示来源；
        // 全局默认经群级覆盖后，再与群管理权限取 AND，和工作流、状态接口保持一致。
        var memoryAllowed = group?.MemoryInjectionEnabled != false;
        var effective = global.WithOverrides(overrides);
        // EffectiveConfig 反映最终生效状态：总开关 + 群响应开关 + 参与择机开关的 AND。
        effective.Enabled = effective.Enabled && isEnabled && (group?.AiProactiveEnabled ?? false);
        effective.MemoryInjectionEnabled = effective.MemoryInjectionEnabled && memoryAllowed;
        return Ok(new
        {
            GroupId = id, Name = known?.Name, IsEnabled = isEnabled,
            Enabled = group?.AiProactiveEnabled ?? false,
            Overrides = overrides,
            EffectiveConfig = effective,
            MemoryAllowed = memoryAllowed
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
            // 与 backfill/入群自动配置一致：新配置行默认参与响应，首次保存参数不能把群静默停用。
            entity = new GroupConfigEntity { GroupId = id, IsEnabled = true };
            db.GroupConfigs.Add(entity);
        }
        if (dto.Overrides is not null)
        {
            var values = dto.Overrides;
            if (!values.HasCompleteQuietHours()) return BadRequest(new { error = "静默开始与结束时间需要一起设置或一起恢复全局" });
            await NormalizeAsync(values, ct);
            var json = ProactiveOverridesCodec.Encode(values);
            if (Encoding.UTF8.GetByteCount(json) > MaxOverridesJsonBytes)
                return BadRequest(new { error = "参数内容过大，请减少自定义项" });
            entity.ProactiveOverridesJson = json;
        }
        if (dto.Enabled.HasValue) entity.AiProactiveEnabled = dto.Enabled.Value;
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
        if (await db.ProactivePresets.CountAsync(ct) >= MaxProactivePresets)
            return BadRequest(new { error = $"最多保存 {MaxProactivePresets} 个参数预设，请先删除不用的预设" });
        var name = CleanText(dto.Name);
        var description = CleanText(dto.Description);
        if (name.Length is < 1 or > 24 || description.Length > 120)
            return BadRequest(new { error = "预设名称需为 1–24 字，说明最多 120 字" });
        var values = dto.Values ?? new ProactiveOverrides();
        // 与群参数保存一致：半个静默区间是无效输入，先于 Normalize 拒绝，
        // 否则 Normalize 会静默清空两个键，客户端误以为保存成功。
        if (!values.HasCompleteQuietHours()) return BadRequest(new { error = "静默开始与结束时间需要一起保存" });
        values.Normalize(); // 全局限制键与空预设在这里被清空/识别
        if (!values.HasValues()) return BadRequest(new { error = "预设至少需要包含一项参数" });
        if (await NameTakenAsync(name, excludeId: null, ct)) return Conflict(new { error = "这个预设名称已存在，请换一个名称" });
        var valuesJson = await EncodeValuesAsync(values, ct);
        if (valuesJson is null) return BadRequest(new { error = "参数内容过大，请减少自定义项" });
        var entity = new ProactivePresetEntity
        {
            Name = name, Description = description, ValuesJson = valuesJson, UpdatedAt = _clock.GetUtcNow().UtcDateTime
        };
        db.ProactivePresets.Add(entity);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            return Conflict(new { error = "这个预设名称已存在，请换一个名称" });
        }
        return Ok(ToDto(entity));
    }

    /// <summary>更新预设的名称/说明/参数；缺省 values 保留原参数。</summary>
    [HttpPut("presets/{id:int}")]
    public async Task<IActionResult> UpdatePreset(int id, [FromBody] PresetWriteDto dto, CancellationToken ct)
    {
        var entity = await db.ProactivePresets.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return NotFound(new { error = "这条预设已不存在" });
        var name = CleanText(dto.Name);
        var description = CleanText(dto.Description);
        if (name.Length is < 1 or > 24 || description.Length > 120)
            return BadRequest(new { error = "预设名称需为 1–24 字，说明最多 120 字" });
        if (await NameTakenAsync(name, excludeId: id, ct)) return Conflict(new { error = "这个预设名称已存在，请换一个名称" });
        if (dto.Values is { } incoming)
        {
            // 先拒绝半区间再 Normalize，避免无效输入被静默清空后当成保存成功。
            if (!incoming.HasCompleteQuietHours()) return BadRequest(new { error = "静默开始与结束时间需要一起保存" });
            incoming.Normalize(); // 与创建一致：全局限制键在此丢弃
            if (!incoming.HasValues()) return BadRequest(new { error = "预设至少需要包含一项参数" });
            var valuesJson = await EncodeValuesAsync(incoming, ct);
            if (valuesJson is null) return BadRequest(new { error = "参数内容过大，请减少自定义项" });
            entity.ValuesJson = valuesJson;
        }
        entity.Name = name;
        entity.Description = description;
        entity.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
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

    /// <summary>名称与说明去除控制字符后 Trim，再交给各处校验长度。</summary>
    private static string CleanText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Trim();
    }

    /// <summary>重名判断包含内置预设；更新时排除自身。预设最多 50 条，内存比较足够。</summary>
    private async Task<bool> NameTakenAsync(string name, int? excludeId, CancellationToken ct)
    {
        if (BuiltIns().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return true;
        var names = await db.ProactivePresets.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        return names.Any(p => p.Id != excludeId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> EncodeValuesAsync(ProactiveOverrides values, CancellationToken ct)
    {
        await NormalizeAsync(values, ct);
        var json = ProactiveOverridesCodec.Encode(values);
        return Encoding.UTF8.GetByteCount(json) > MaxOverridesJsonBytes ? null : json;
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
