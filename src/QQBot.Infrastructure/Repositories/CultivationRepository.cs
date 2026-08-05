using Microsoft.EntityFrameworkCore;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Infrastructure.Data;
using QQBot.Infrastructure.Entities;

namespace QQBot.Infrastructure.Repositories;

public class CultivationRepository : ICultivationRepository
{
    private readonly AppDbContext _db;

    public CultivationRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PlayerProfile?> GetPlayerAsync(string groupId, string memberOpenId, CancellationToken ct = default)
    {
        var e = await _db.PlayerProfiles
            .FirstOrDefaultAsync(p => p.GroupId == groupId && p.MemberOpenId == memberOpenId, ct);
        return e is null ? null : ToDomain(e);
    }

    public async Task<PlayerProfile> GetOrCreatePlayerAsync(string groupId, string memberOpenId, string nickname, CancellationToken ct = default)
    {
        var e = await _db.PlayerProfiles
            .FirstOrDefaultAsync(p => p.GroupId == groupId && p.MemberOpenId == memberOpenId, ct);

        if (e is not null)
        {
            // 更新昵称（消息中可能拿到更准确的昵称）
            if (!string.IsNullOrEmpty(nickname) && e.Nickname != nickname)
                e.Nickname = nickname;
            await _db.SaveChangesAsync(ct);
            return ToDomain(e);
        }

        // 创角：随机灵根（类型 0-6，品级按权重分布，越差越常见）
        var rng = new Random();
        var rootType = rng.Next(0, 7);
        var grade = rng.NextDouble() switch
        {
            < 0.02 => 1,
            < 0.10 => 2,
            < 0.30 => 3,
            < 0.60 => 4,
            _ => 5
        };

        e = new PlayerProfileEntity
        {
            GroupId = groupId,
            MemberOpenId = memberOpenId,
            Nickname = nickname,
            RealmIndex = 0,
            CultivationPoints = 0,
            SpiritRootType = rootType,
            SpiritRootGrade = grade,
            TechniqueId = null,
            LastSettledAt = DateTime.UtcNow,
            IdleRateMultiplier = 1.0,
            SpiritStones = 0,
            Lifespan = 200,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _db.PlayerProfiles.Add(e);
        await _db.SaveChangesAsync(ct);
        return ToDomain(e);
    }

    public async Task SavePlayerAsync(PlayerProfile player, CancellationToken ct = default)
    {
        var e = await _db.PlayerProfiles
            .FirstOrDefaultAsync(p => p.GroupId == player.GroupId && p.MemberOpenId == player.MemberOpenId, ct);
        if (e is null) return;

        e.RealmIndex = player.RealmIndex;
        e.CultivationPoints = player.CultivationPoints;
        e.SpiritRootType = player.SpiritRootType;
        e.SpiritRootGrade = player.SpiritRootGrade;
        e.TechniqueId = player.TechniqueId;
        e.PendingBreakthroughBonus = player.PendingBreakthroughBonus;
        e.LastSettledAt = player.LastSettledAt;
        e.IdleRateMultiplier = player.IdleRateMultiplier;
        e.SpiritStones = player.SpiritStones;
        e.Lifespan = player.Lifespan;
        e.CheckinStreak = player.CheckinStreak;
        e.LastCheckinAt = player.LastCheckinAt;
        e.Nickname = player.Nickname;
        e.UpdatedAt = DateTime.UtcNow;
        e.IsActive = player.IsActive;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<GroupWorld> GetOrCreateGroupWorldAsync(string groupId, CancellationToken ct = default)
    {
        var e = await _db.GroupWorlds.FirstOrDefaultAsync(w => w.GroupId == groupId, ct);
        if (e is not null) return ToDomain(e);

        e = new GroupWorldEntity
        {
            GroupId = groupId,
            WorldQiLevel = 1,
            WorldQiUpdatedAt = DateTime.UtcNow,
            LastEventGeneratedAt = DateTime.UtcNow,
            LeaderboardSnapshotAt = DateTime.UtcNow
        };
        _db.GroupWorlds.Add(e);
        await _db.SaveChangesAsync(ct);
        return ToDomain(e);
    }

    public async Task SaveGroupWorldAsync(GroupWorld world, CancellationToken ct = default)
    {
        var e = await _db.GroupWorlds.FirstOrDefaultAsync(w => w.GroupId == world.GroupId, ct);
        if (e is null)
        {
            e = new GroupWorldEntity { GroupId = world.GroupId };
            _db.GroupWorlds.Add(e);
        }

        e.WorldQiLevel = world.WorldQiLevel;
        e.WorldQiUpdatedAt = world.WorldQiUpdatedAt;
        e.PendingEventJson = world.PendingEventJson;
        e.PendingEventExpiresAt = world.PendingEventExpiresAt;
        e.PendingEventParticipants = world.PendingEventParticipants;
        e.LastEventGeneratedAt = world.LastEventGeneratedAt;
        e.LeaderboardSnapshotAt = world.LeaderboardSnapshotAt;
        e.TribulationSessionJson = world.TribulationSessionJson;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<PlayerInventoryItem>> GetInventoryAsync(string groupId, string memberOpenId, CancellationToken ct = default)
    {
        return await _db.PlayerInventories
            .Where(i => i.GroupId == groupId && i.MemberOpenId == memberOpenId && i.Quantity > 0)
            .Select(i => new PlayerInventoryItem(i.ItemId, i.ItemType, i.ItemName, i.Quantity))
            .ToListAsync(ct);
    }

    public async Task UpdateInventoryItemAsync(string groupId, string memberOpenId, string itemId, string itemType, string itemName, int delta, CancellationToken ct = default)
    {
        var e = await _db.PlayerInventories
            .FirstOrDefaultAsync(i => i.GroupId == groupId && i.MemberOpenId == memberOpenId && i.ItemId == itemId, ct);

        if (e is null)
        {
            if (delta <= 0) return; // 不存在且要扣减，无需操作
            e = new PlayerInventoryEntity
            {
                GroupId = groupId,
                MemberOpenId = memberOpenId,
                ItemId = itemId,
                ItemType = itemType,
                ItemName = itemName,
                Quantity = delta,
                UpdatedAt = DateTime.UtcNow
            };
            _db.PlayerInventories.Add(e);
        }
        else
        {
            e.Quantity = Math.Max(0, e.Quantity + delta);
            if (!string.IsNullOrEmpty(itemName)) e.ItemName = itemName;
            if (!string.IsNullOrEmpty(itemType)) e.ItemType = itemType;
            e.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<DateTime?> GetCooldownAsync(string groupId, string memberOpenId, string actionType, CancellationToken ct = default)
    {
        var e = await _db.GameCooldowns
            .FirstOrDefaultAsync(c => c.GroupId == groupId && c.MemberOpenId == memberOpenId && c.ActionType == actionType, ct);
        return e?.AvailableAt;
    }

    public async Task SetCooldownAsync(string groupId, string memberOpenId, string actionType, TimeSpan duration, CancellationToken ct = default)
    {
        var e = await _db.GameCooldowns
            .FirstOrDefaultAsync(c => c.GroupId == groupId && c.MemberOpenId == memberOpenId && c.ActionType == actionType, ct);

        var availableAt = DateTime.UtcNow + duration;
        if (e is null)
        {
            _db.GameCooldowns.Add(new GameCooldownEntity
            {
                GroupId = groupId,
                MemberOpenId = memberOpenId,
                ActionType = actionType,
                AvailableAt = availableAt,
                CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            e.AvailableAt = availableAt;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task AppendEventLogAsync(CultivationEventLog log, CancellationToken ct = default)
    {
        _db.CultivationEventLogs.Add(new CultivationEventLogEntity
        {
            GroupId = log.GroupId,
            MemberOpenId = log.MemberOpenId,
            EventType = log.EventType,
            DetailJson = log.DetailJson,
            AiRawOutput = log.AiRawOutput,
            AppliedChangesJson = log.AppliedChangesJson,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<LeaderboardEntry>> GetGroupLeaderboardAsync(string groupId, int top, CancellationToken ct = default)
    {
        return await _db.PlayerProfiles
            .Where(p => p.GroupId == groupId && p.IsActive)
            .OrderByDescending(p => p.CultivationPoints)
            .ThenByDescending(p => p.RealmIndex)
            .Take(top)
            .Select(p => new LeaderboardEntry(p.MemberOpenId, p.Nickname, p.RealmIndex, p.CultivationPoints))
            .ToListAsync(ct);
    }

    public async Task<List<CultivationEventLogEntry>> GetEventLogsAsync(string groupId, int top, CancellationToken ct = default)
    {
        return await _db.CultivationEventLogs
            .Where(e => e.GroupId == groupId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(top)
            .Select(e => new CultivationEventLogEntry(
                e.Id, e.GroupId, e.MemberOpenId, e.EventType,
                e.DetailJson, e.AiRawOutput, e.AppliedChangesJson, e.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<List<string>> GetActiveGroupIdsAsync(CancellationToken ct = default)
    {
        return await _db.PlayerProfiles
            .Where(p => p.IsActive)
            .Select(p => p.GroupId)
            .Distinct()
            .ToListAsync(ct);
    }

    private static PlayerProfile ToDomain(PlayerProfileEntity e) => new()
    {
        Id = e.Id,
        GroupId = e.GroupId,
        MemberOpenId = e.MemberOpenId,
        Nickname = e.Nickname,
        RealmIndex = e.RealmIndex,
        CultivationPoints = e.CultivationPoints,
        SpiritRootType = e.SpiritRootType,
        SpiritRootGrade = e.SpiritRootGrade,
        TechniqueId = e.TechniqueId,
        PendingBreakthroughBonus = e.PendingBreakthroughBonus,
        LastSettledAt = e.LastSettledAt,
        IdleRateMultiplier = e.IdleRateMultiplier,
        SpiritStones = e.SpiritStones,
        Lifespan = e.Lifespan,
        CheckinStreak = e.CheckinStreak,
        LastCheckinAt = e.LastCheckinAt,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        IsActive = e.IsActive
    };

    private static GroupWorld ToDomain(GroupWorldEntity e) => new()
    {
        GroupId = e.GroupId,
        WorldQiLevel = e.WorldQiLevel,
        WorldQiUpdatedAt = e.WorldQiUpdatedAt,
        PendingEventJson = e.PendingEventJson,
        PendingEventExpiresAt = e.PendingEventExpiresAt,
        PendingEventParticipants = e.PendingEventParticipants,
        LastEventGeneratedAt = e.LastEventGeneratedAt,
        LeaderboardSnapshotAt = e.LeaderboardSnapshotAt,
        TribulationSessionJson = e.TribulationSessionJson
    };
}
