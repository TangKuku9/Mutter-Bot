using Microsoft.EntityFrameworkCore;
using QQBot.Core.Models;
using QQBot.Infrastructure.Entities;

namespace QQBot.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<GroupInfo> GroupInfos => Set<GroupInfo>();
    public DbSet<GroupConfigEntity> GroupConfigs => Set<GroupConfigEntity>();
    public DbSet<AiConfigEntity> AiConfigs => Set<AiConfigEntity>();
    public DbSet<PrivateChatConfigEntity> PrivateChatConfigs => Set<PrivateChatConfigEntity>();
    public DbSet<KnownGroupEntity> KnownGroups => Set<KnownGroupEntity>();
    public DbSet<KnownUserEntity> KnownUsers => Set<KnownUserEntity>();
    public DbSet<ActiveMessageQuotaEntity> ActiveMessageQuotas => Set<ActiveMessageQuotaEntity>();

    // 修仙RPG 游戏域
    public DbSet<PlayerProfileEntity> PlayerProfiles => Set<PlayerProfileEntity>();
    public DbSet<GroupWorldEntity> GroupWorlds => Set<GroupWorldEntity>();
    public DbSet<PlayerInventoryEntity> PlayerInventories => Set<PlayerInventoryEntity>();
    public DbSet<GameCooldownEntity> GameCooldowns => Set<GameCooldownEntity>();
    public DbSet<CultivationEventLogEntity> CultivationEventLogs => Set<CultivationEventLogEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qqbot.db");
        options.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ChatType, x.GroupId, x.CreatedAt });
            e.HasIndex(x => new { x.ChatType, x.UserId, x.CreatedAt });
            e.HasIndex(x => x.MessageId).IsUnique().HasFilter("\"MessageId\" <> ''");
        });

        modelBuilder.Entity<GroupInfo>(e =>
        {
            e.HasKey(x => x.GroupId);
        });

        modelBuilder.Entity<GroupConfigEntity>(e =>
        {
            e.HasKey(x => x.GroupId);
        });

        modelBuilder.Entity<AiConfigEntity>(e =>
        {
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PrivateChatConfigEntity>(e =>
        {
            e.HasKey(x => x.UserId);
        });

        modelBuilder.Entity<KnownGroupEntity>(e =>
        {
            e.HasKey(x => x.GroupOpenId);
        });

        modelBuilder.Entity<KnownUserEntity>(e =>
        {
            e.HasKey(x => x.UserOpenId);
        });

        modelBuilder.Entity<ActiveMessageQuotaEntity>(e =>
        {
            e.HasKey(x => new { x.Key, x.Scope, x.Month });
        });

        // 修仙RPG 游戏域
        modelBuilder.Entity<PlayerProfileEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.MemberOpenId }).IsUnique();
            e.HasIndex(x => new { x.GroupId, x.CultivationPoints });
            // 乐观并发令牌：UPDATE/DELETE 时把 Version 加入 WHERE，
            // 一旦该行被其它写改过即 0 行受影响 -> DbUpdateConcurrencyException
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<GroupWorldEntity>(e =>
        {
            e.HasKey(x => x.GroupId);
        });

        modelBuilder.Entity<PlayerInventoryEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.MemberOpenId, x.ItemId }).IsUnique();
        });

        modelBuilder.Entity<GameCooldownEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.MemberOpenId, x.ActionType });
        });

        modelBuilder.Entity<CultivationEventLogEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.MemberOpenId, x.CreatedAt });
            e.HasIndex(x => new { x.GroupId, x.EventType, x.CreatedAt });
        });
    }

    public async Task EnsureSchemaUpgradedAsync(CancellationToken ct = default)
    {
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "KnownGroups" (
                "GroupOpenId" TEXT NOT NULL CONSTRAINT "PK_KnownGroups" PRIMARY KEY,
                "Name" TEXT,
                "AddedAt" TEXT NOT NULL
            );
            """, ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "KnownUsers" (
                "UserOpenId" TEXT NOT NULL CONSTRAINT "PK_KnownUsers" PRIMARY KEY,
                "Nickname" TEXT,
                "AddedAt" TEXT NOT NULL
            );
            """, ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ActiveMessageQuotas" (
                "Key" TEXT NOT NULL,
                "Scope" TEXT NOT NULL,
                "Month" TEXT NOT NULL,
                "Count" INTEGER NOT NULL,
                "Rejected" INTEGER NOT NULL,
                CONSTRAINT "PK_ActiveMessageQuotas" PRIMARY KEY ("Key", "Scope", "Month")
            );
            """, ct);

        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ActiveMessageQuotas\" ADD COLUMN \"LastSentAt\" TEXT", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // AiConfigs 表增加联网搜索开关列（兼容已有数据库）
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"AiConfigs\" ADD COLUMN \"WebSearchEnabled\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // ===== 修仙RPG 游戏域建表（兼容已有数据库） =====
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "PlayerProfiles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PlayerProfiles" PRIMARY KEY AUTOINCREMENT,
                "GroupId" TEXT NOT NULL,
                "MemberOpenId" TEXT NOT NULL,
                "Nickname" TEXT NOT NULL DEFAULT '',
                "RealmIndex" INTEGER NOT NULL DEFAULT 0,
                "CultivationPoints" INTEGER NOT NULL DEFAULT 0,
                "SpiritRootType" INTEGER NOT NULL DEFAULT 0,
                "SpiritRootGrade" INTEGER NOT NULL DEFAULT 3,
                "TechniqueId" TEXT NULL,
                "LastSettledAt" TEXT NOT NULL,
                "IdleRateMultiplier" REAL NOT NULL DEFAULT 1.0,
                "SpiritStones" INTEGER NOT NULL DEFAULT 0,
                "Lifespan" INTEGER NOT NULL DEFAULT 100,
                "CheckinStreak" INTEGER NOT NULL DEFAULT 0,
                "LastCheckinAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL DEFAULT 1
            );
            """, ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PlayerProfiles_GroupId_MemberOpenId\" ON \"PlayerProfiles\" (\"GroupId\", \"MemberOpenId\")", ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PlayerProfiles_GroupId_CultivationPoints\" ON \"PlayerProfiles\" (\"GroupId\", \"CultivationPoints\" DESC)", ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "GroupWorlds" (
                "GroupId" TEXT NOT NULL CONSTRAINT "PK_GroupWorlds" PRIMARY KEY,
                "WorldQiLevel" INTEGER NOT NULL DEFAULT 1,
                "WorldQiUpdatedAt" TEXT NOT NULL,
                "PendingEventJson" TEXT NULL,
                "PendingEventExpiresAt" TEXT NULL,
                "PendingEventParticipants" INTEGER NOT NULL DEFAULT 0,
                "LastEventGeneratedAt" TEXT NOT NULL,
                "LeaderboardSnapshotAt" TEXT NOT NULL
            );
            """, ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "PlayerInventories" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PlayerInventories" PRIMARY KEY AUTOINCREMENT,
                "GroupId" TEXT NOT NULL,
                "MemberOpenId" TEXT NOT NULL,
                "ItemId" TEXT NOT NULL,
                "ItemType" TEXT NOT NULL DEFAULT '',
                "ItemName" TEXT NOT NULL DEFAULT '',
                "Quantity" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAt" TEXT NOT NULL
            );
            """, ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PlayerInventories_GroupId_MemberOpenId_ItemId\" ON \"PlayerInventories\" (\"GroupId\", \"MemberOpenId\", \"ItemId\")", ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "GameCooldowns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_GameCooldowns" PRIMARY KEY AUTOINCREMENT,
                "GroupId" TEXT NOT NULL,
                "MemberOpenId" TEXT NOT NULL,
                "ActionType" TEXT NOT NULL,
                "AvailableAt" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_GameCooldowns_GroupId_MemberOpenId_ActionType\" ON \"GameCooldowns\" (\"GroupId\", \"MemberOpenId\", \"ActionType\")", ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "CultivationEventLogs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CultivationEventLogs" PRIMARY KEY AUTOINCREMENT,
                "GroupId" TEXT NOT NULL,
                "MemberOpenId" TEXT NULL,
                "EventType" TEXT NOT NULL DEFAULT '',
                "DetailJson" TEXT NOT NULL DEFAULT '',
                "AiRawOutput" TEXT NULL,
                "AppliedChangesJson" TEXT NOT NULL DEFAULT '',
                "CreatedAt" TEXT NOT NULL
            );
            """, ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_CultivationEventLogs_GroupId_MemberOpenId_CreatedAt\" ON \"CultivationEventLogs\" (\"GroupId\", \"MemberOpenId\", \"CreatedAt\")", ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_CultivationEventLogs_GroupId_EventType_CreatedAt\" ON \"CultivationEventLogs\" (\"GroupId\", \"EventType\", \"CreatedAt\")", ct);

        // GroupWorlds 表增加渡劫护法会话列（兼容已有数据库）
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"GroupWorlds\" ADD COLUMN \"TribulationSessionJson\" TEXT", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // PlayerProfiles 表增加待消耗突破加成列（兼容已有数据库）
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PlayerProfiles\" ADD COLUMN \"PendingBreakthroughBonus\" REAL NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // PlayerProfiles 表增加乐观并发令牌列（兼容已有数据库）
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PlayerProfiles\" ADD COLUMN \"Version\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }
    }
}
