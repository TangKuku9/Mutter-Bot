using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;

namespace Mutter.Web.Controllers;

/// <summary>
/// 群配置 / 私聊白名单 / 主动消息配额管理接口。
/// </summary>
[ApiController]
[Route("api/groups")]
public class GroupsController : ControllerBase
{
    private readonly AppDbContext _db;

    public GroupsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>已知群列表（合并群配置）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetGroups(CancellationToken ct)
    {
        var groups = await _db.KnownGroups.AsNoTracking().ToListAsync(ct);
        var configs = await _db.GroupConfigs.AsNoTracking().ToDictionaryAsync(c => c.GroupId, ct);

        var result = groups.Select(g =>
        {
            configs.TryGetValue(g.GroupOpenId, out var cfg);
            return new
            {
                g.GroupOpenId,
                g.Name,
                g.AddedAt,
                Config = cfg is null ? null : new
                {
                    cfg.IsEnabled,
                    cfg.TriggerMode,
                    cfg.AiProactiveEnabled,
                    cfg.BotMentionOpenId,
                    cfg.ActiveCharacter,
                    cfg.CustomTemperature,
                    cfg.CustomSystemPrompt,
                    cfg.MemoryInjectionEnabled,
                }
            };
        }).OrderByDescending(x => x.AddedAt).ToList();

        return Ok(result);
    }

    public class GroupConfigUpdateDto
    {
        /// <summary>人工维护的群名；空串或空白表示清空。</summary>
        public string? Name { get; set; }

        public bool IsEnabled { get; set; }
        public int TriggerMode { get; set; }
        public bool AiProactiveEnabled { get; set; }
        public string? BotMentionOpenId { get; set; }
        public string? ActiveCharacter { get; set; }
        public double? CustomTemperature { get; set; }
        public string? CustomSystemPrompt { get; set; }

        /// <summary>群级群记忆注入覆盖：null = 跟随全局。</summary>
        public bool? MemoryInjectionEnabled { get; set; }
        public bool UpdateMemory { get; set; }
        public string? GroupMemory { get; set; }
    }

    /// <summary>更新指定群的配置（不存在则创建）。</summary>
    [HttpPut("{groupId}/config")]
    public async Task<IActionResult> UpdateGroupConfig(string groupId, [FromBody] GroupConfigUpdateDto dto, CancellationToken ct)
    {
        var entity = await _db.GroupConfigs.FirstOrDefaultAsync(c => c.GroupId == groupId, ct);
        if (entity is null)
        {
            entity = new GroupConfigEntity { GroupId = groupId };
            _db.GroupConfigs.Add(entity);
        }

        entity.IsEnabled = dto.IsEnabled;
        entity.TriggerMode = dto.TriggerMode == 2 ? 2 : 0;
        entity.AiProactiveEnabled = dto.AiProactiveEnabled;
        entity.BotMentionOpenId = string.IsNullOrWhiteSpace(dto.BotMentionOpenId) ? null : dto.BotMentionOpenId.Trim();
        entity.ActiveCharacter = dto.ActiveCharacter;
        entity.CustomTemperature = dto.CustomTemperature is { } temperature && double.IsFinite(temperature)
            ? Math.Clamp(temperature, 0, 2) : null;
        entity.CustomSystemPrompt = dto.CustomSystemPrompt;
        entity.MemoryInjectionEnabled = dto.MemoryInjectionEnabled;

        // QQ 不向普通机器人下发群名称，这里保存的是面板里人工起的名字
        var knownGroup = await _db.KnownGroups.FirstOrDefaultAsync(g => g.GroupOpenId == groupId, ct);
        if (knownGroup is null)
        {
            knownGroup = new KnownGroupEntity { GroupOpenId = groupId };
            _db.KnownGroups.Add(knownGroup);
        }
        knownGroup.Name = string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim();

        // 群设置、名称和记忆共用一次 SaveChanges 的事务，任何一步失败都不落库。
        if (dto.UpdateMemory)
        {
            var memory = await _db.GroupMemories.SingleOrDefaultAsync(m => m.GroupId == groupId, ct);
            if (memory is null)
            {
                memory = new GroupMemoryEntity { GroupId = groupId };
                _db.GroupMemories.Add(memory);
            }
            memory.Summary = GroupMemoryRepository.NormalizeSummary(dto.GroupMemory);
            memory.Version++;
            memory.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "群配置已保存" });
    }

    /// <summary>私聊白名单列表（合并已知用户昵称）。</summary>
    [HttpGet("private-chats")]
    public async Task<IActionResult> GetPrivateChats(CancellationToken ct)
    {
        var configs = await _db.PrivateChatConfigs.AsNoTracking().ToListAsync(ct);
        var users = await _db.KnownUsers.AsNoTracking().ToListAsync(ct);

        var result = configs.Select(c => new
        {
            c.UserId,
            Nickname = users.FirstOrDefault(u => u.UserOpenId == c.UserId)?.Nickname,
            c.IsEnabled,
            c.IsAdmin
        }).ToList();

        return Ok(result);
    }

    public class PrivateChatConfigDto
    {
        public bool IsEnabled { get; set; }
        public bool IsAdmin { get; set; }
    }

    /// <summary>新增/更新私聊白名单用户。</summary>
    [HttpPut("private-chats/{userId}")]
    public async Task<IActionResult> UpsertPrivateChat(string userId, [FromBody] PrivateChatConfigDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { error = "缺少用户 ID" });

        var entity = await _db.PrivateChatConfigs.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity is null)
        {
            entity = new PrivateChatConfigEntity { UserId = userId };
            _db.PrivateChatConfigs.Add(entity);
        }

        entity.IsEnabled = dto.IsEnabled;
        entity.IsAdmin = dto.IsAdmin;

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "私聊配置已保存" });
    }

    /// <summary>从私聊白名单移除用户。</summary>
    [HttpDelete("private-chats/{userId}")]
    public async Task<IActionResult> DeletePrivateChat(string userId, CancellationToken ct)
    {
        var entity = await _db.PrivateChatConfigs.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity is null)
            return NotFound(new { error = "用户不在白名单中" });

        _db.PrivateChatConfigs.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "已移除" });
    }

    /// <summary>主动消息用量（按月累计行）。分钟/每日实时余量见 /api/proactive/status。</summary>
    [HttpGet("quotas")]
    public async Task<IActionResult> GetQuotas(CancellationToken ct)
    {
        var quotas = await _db.ActiveMessageQuotas.AsNoTracking()
            .OrderByDescending(q => q.Month)
            .ThenBy(q => q.Key)
            .Take(200)
            .ToListAsync(ct);

        return Ok(quotas.Select(q => new
        {
            q.Key,
            q.Scope,
            q.Month,
            q.Count,
            q.Rejected,
            q.LastSentAt
        }));
    }
}
