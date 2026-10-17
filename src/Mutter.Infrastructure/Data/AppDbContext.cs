using Microsoft.EntityFrameworkCore;
using Mutter.Core.Models;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Data;

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
    public DbSet<CustomCharacterEntity> CustomCharacters => Set<CustomCharacterEntity>();
    public DbSet<ProactiveChatConfigEntity> ProactiveChatConfigs => Set<ProactiveChatConfigEntity>();
    public DbSet<ProactiveChatLogEntity> ProactiveChatLogs => Set<ProactiveChatLogEntity>();
    public DbSet<GroupMemoryEntity> GroupMemories => Set<GroupMemoryEntity>();

    // 修仙RPG 的 5 张表（PlayerProfiles / GroupWorlds / PlayerInventories /
    // GameCooldowns / CultivationEventLogs）保留建表语句与补列迁移，但不再建实体：
    // 功能已拆到 xianxia 分支，主分支只保证已有部署的库结构不被破坏、数据不丢。
    // 将来若在修仙分支继续迭代，这里的 DDL 需要与那边的实体保持同步。

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mutter-bot.db");
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

        modelBuilder.Entity<CustomCharacterEntity>(e =>
        {
            e.HasKey(x => x.Name);
        });

        modelBuilder.Entity<ProactiveChatConfigEntity>(e =>
        {
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ProactiveChatLogEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EvaluatedAt);
            e.HasIndex(x => new { x.GroupId, x.EvaluatedAt });
        });

        modelBuilder.Entity<GroupMemoryEntity>(e =>
        {
            e.HasKey(x => x.GroupId);
            e.Property(x => x.Summary).IsRequired();
            e.HasIndex(x => x.UpdatedAt);
        });
    }

    public async Task EnsureSchemaUpgradedAsync(CancellationToken ct = default)
    {
        // 历史遗留修复：GroupConfigs / PrivateChatConfigs 是从 Windows 版带过来的，主键建成了
        // INTEGER AUTOINCREMENT（那时的群号是纯数字）。QQ 官方现在下发的是十六进制 openid，
        // 而 EF 按 string 映射这两个键 → 任何新群/新好友的配置写入都会 "datatype mismatch" 500。
        await RepairIntegerKeyAsync("GroupConfigs", "GroupId", ct);
        await RepairIntegerKeyAsync("PrivateChatConfigs", "UserId", ct);

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

        // @ 回复 / 私聊的附图张数。老库补成 3，与它此前"固定只带 ImageUrls[0]"的行为相比是放宽，
        // 但仍是可用区间内的保守值。
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"AiConfigs\" ADD COLUMN \"MaxAttachedImages\" INTEGER NOT NULL DEFAULT 3", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 已选用的联网搜索策略：探测结果“选用”后落库，运行时优先于按 URL 推断。
        // 老库补空值，行为与升级前一致（继续按地址推断）。
        foreach (var sql in new[]
                 {
                     "ALTER TABLE \"AiConfigs\" ADD COLUMN \"WebSearchEndpoint\" TEXT NOT NULL DEFAULT ''",
                     "ALTER TABLE \"AiConfigs\" ADD COLUMN \"WebSearchShape\" TEXT NOT NULL DEFAULT ''",
                     "ALTER TABLE \"AiConfigs\" ADD COLUMN \"WebSearchVerifiedAt\" TEXT"
                 })
        {
            try
            {
                await Database.ExecuteSqlRawAsync(sql, ct);
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
            {
            }
        }

        // 自定义人设表（兼容已有数据库）
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "CustomCharacters" (
                "Name" TEXT NOT NULL CONSTRAINT "PK_CustomCharacters" PRIMARY KEY,
                "DisplayName" TEXT NOT NULL DEFAULT '',
                "Description" TEXT NOT NULL DEFAULT '',
                "SystemPrompt" TEXT NOT NULL DEFAULT '',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, ct);

        // 群记忆：每群一条人工维护的摘要，择机发言判定时作背景参考。
        // 幂等建表，兼容没有这张表的历史库；不回填，旧库升级后从"没有记忆"开始。
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "GroupMemories" (
                "GroupId" TEXT NOT NULL CONSTRAINT "PK_GroupMemories" PRIMARY KEY,
                "Summary" TEXT NOT NULL DEFAULT '',
                "UpdatedAt" TEXT NOT NULL,
                "Version" INTEGER NOT NULL DEFAULT 1
            );
            """, ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_GroupMemories_UpdatedAt\" ON \"GroupMemories\" (\"UpdatedAt\")", ct);

        // ===== 修仙RPG 游戏域建表（主分支不再使用，仅保留结构） =====
        // 功能已拆到 xianxia 分支。这里刻意保留建表与补列：
        //   1) 已有部署的库里这 5 张表可能存着玩家数据，删表不可逆；
        //   2) 切回修仙分支时无需再补 schema。
        // 主分支不对它们做任何读写，纯粹是"留着不碍事"。
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

        // ===== 择机主动发言 =====
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ProactiveChatConfigs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProactiveChatConfigs" PRIMARY KEY,
                "Enabled" INTEGER NOT NULL DEFAULT 0,
                "WindowSeconds" INTEGER NOT NULL DEFAULT 60,
                "WindowMaxMessages" INTEGER NOT NULL DEFAULT 12,
                "CooldownMinutes" INTEGER NOT NULL DEFAULT 45,
                "MaxPerGroupPerDay" INTEGER NOT NULL DEFAULT 3,
                "MaxGlobalPerDay" INTEGER NOT NULL DEFAULT 12,
                "QuietStartHour" INTEGER NOT NULL DEFAULT 23,
                "QuietEndHour" INTEGER NOT NULL DEFAULT 8,
                "Liveliness" INTEGER NOT NULL DEFAULT 1,
                "MaxAttachedImages" INTEGER NOT NULL DEFAULT 3,
                "HistoryMaxMessages" INTEGER NOT NULL DEFAULT 8,
                "EvalMaxTokens" INTEGER NOT NULL DEFAULT 512,
                "AllowActiveChannel" INTEGER NOT NULL DEFAULT 1,
                "ActivePerGroupPerMinute" INTEGER NOT NULL DEFAULT 3,
                "ActivePerGroupPerDay" INTEGER NOT NULL DEFAULT 20,
                "ActivePerAccountPerMinute" INTEGER NOT NULL DEFAULT 20,
                "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'
            );
            """, ct);

        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ProactiveChatLogs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProactiveChatLogs" PRIMARY KEY AUTOINCREMENT,
                "GroupId" TEXT NOT NULL,
                "EvaluatedAt" TEXT NOT NULL,
                "MessageCount" INTEGER NOT NULL DEFAULT 0,
                "ImageCount" INTEGER NOT NULL DEFAULT 0,
                "MemoryCount" INTEGER NOT NULL DEFAULT 0,
                "MemoryPromptChars" INTEGER NOT NULL DEFAULT 0,
                "Outcome" TEXT NOT NULL DEFAULT '',
                "ShouldSpeak" INTEGER NOT NULL DEFAULT 0,
                "Topic" TEXT,
                "Line" TEXT,
                "Reason" TEXT,
                "Detail" TEXT,
                "SentMessageId" TEXT,
                "Model" TEXT,
                "PromptTokens" INTEGER NOT NULL DEFAULT 0,
                "CompletionTokens" INTEGER NOT NULL DEFAULT 0
            );
            """, ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_ProactiveChatLogs_EvaluatedAt\" ON \"ProactiveChatLogs\" (\"EvaluatedAt\")", ct);
        await Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_ProactiveChatLogs_GroupId_EvaluatedAt\" ON \"ProactiveChatLogs\" (\"GroupId\", \"EvaluatedAt\")", ct);

        // ChatMessages 标记“Bot 主动插话”，面板据此把择机发言与被动应答分开展示
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ChatMessages\" ADD COLUMN \"IsProactive\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 择机发言的群级参与位
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"GroupConfigs\" ADD COLUMN \"AiProactiveEnabled\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 发言倾向（0 克制 / 1 正常 / 2 主动）。老库补成"正常"，不改用户已保存的值
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatConfigs\" ADD COLUMN \"Liveliness\" INTEGER NOT NULL DEFAULT 1", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 择机自己的规模参数：附图张数 / 背景历史条数 / 输出上限。
        // 它们此前分别硬编码、或借 AI 面板的全局值，老库补成与原来等价的默认值。
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatConfigs\" ADD COLUMN \"MaxAttachedImages\" INTEGER NOT NULL DEFAULT 3", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatConfigs\" ADD COLUMN \"HistoryMaxMessages\" INTEGER NOT NULL DEFAULT 8", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatConfigs\" ADD COLUMN \"EvalMaxTokens\" INTEGER NOT NULL DEFAULT 512", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 群记忆注入开关。老库补成 1（开着）—— 群记忆上线时是"有摘要就注入"，
        // 补成 0 等于给所有已填摘要的群静默关掉功能。
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatConfigs\" ADD COLUMN \"MemoryInjectionEnabled\" INTEGER NOT NULL DEFAULT 1", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 群级覆盖：NULL = 跟随全局，所以刻意不给默认值（给了 0 就变成"默认全群停用"）
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"GroupConfigs\" ADD COLUMN \"MemoryInjectionEnabled\" INTEGER", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 私聊 bot 回复的对话对象：没有它就无法把某条回复归到具体某段私聊，多个私聊对象会串味
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ChatMessages\" ADD COLUMN \"PeerUserId\" TEXT", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 机器人群内 @ 识别码：全量模式下这是判断"有没有人 @ 我"的唯一依据，没有就永远不回话
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"GroupConfigs\" ADD COLUMN \"BotMentionOpenId\" TEXT", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 决策留痕记下这次评估实际附带了几张图，否则"bot 看着图却说不清"无从分辨
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatLogs\" ADD COLUMN \"ImageCount\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 同理记下这次判定有没有用群记忆、用了多少字：否则"记忆到底有没有在工作"只能翻代码
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatLogs\" ADD COLUMN \"MemoryCount\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"ProactiveChatLogs\" ADD COLUMN \"MemoryPromptChars\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        // 关键词触发/关键词搭话两条旁路已被择机发言取代，列一并删除（这两项配置在整数主键年代
        // 就没能写进现役群，实际从未生效）。AI 自报的置信度同样删掉：它不是概率、不可校准，
        // 真实拦截力全在 should_speak 这个布尔上，留着一个旋钮只会让人去拧没用的那颗。
        // DROP 失败（列已不存在）按幂等忽略。
        foreach (var ddl in new[]
        {
            "ALTER TABLE \"GroupConfigs\" DROP COLUMN \"Keywords\"",
            "ALTER TABLE \"GroupConfigs\" DROP COLUMN \"ProactiveKeywords\"",
            "ALTER TABLE \"GroupConfigs\" DROP COLUMN \"ProactiveEnabled\"",
            "ALTER TABLE \"ProactiveChatConfigs\" DROP COLUMN \"PersistObservedMessages\"",
            "ALTER TABLE \"ProactiveChatConfigs\" DROP COLUMN \"MinConfidence\"",
            "ALTER TABLE \"ProactiveChatLogs\" DROP COLUMN \"Confidence\""
        })
        {
            try
            {
                await Database.ExecuteSqlRawAsync(ddl, ct);
            }
            catch (Exception ex) when (ex.Message.Contains("no such column")
                                       || ex.Message.Contains("drop column"))
            {
            }
        }

        // 为已在册但从未有配置行的群补一条默认配置（等价于原来的“无配置”行为：仅 @ 响应、
        // 不参与择机发言）。补上后面板才有地方写群级开关，也避免各处 null 判断分叉。
        await Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "GroupConfigs" ("GroupId", "IsEnabled", "TriggerMode", "AiProactiveEnabled")
            SELECT k."GroupOpenId", 1, 0, 0
            FROM "KnownGroups" k
            WHERE NOT EXISTS (SELECT 1 FROM "GroupConfigs" g WHERE g."GroupId" = k."GroupOpenId");
            """, ct);

        // PlayerProfiles 表增加乐观并发令牌列（兼容已有数据库）
        try
        {
            await Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PlayerProfiles\" ADD COLUMN \"Version\" INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name"))
        {
        }

        await BackfillFacePlaceholdersAsync(ct);
    }

    /// <summary>
    /// 把历史上按原文存进库的 &lt;faceType=...&gt; 表情段回填成可读占位。这些原文还会作为
    /// 背景上下文喂给模型，且下游剥 &lt;...&gt; 的规范化会把它变成空串，等于白占 token 又丢信息。
    /// 回填过的行不再匹配 WHERE，所以只跑一次。
    /// </summary>
    private async Task BackfillFacePlaceholdersAsync(CancellationToken ct)
    {
        var connection = Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);

        try
        {
            var rows = new List<(long Id, string Text, bool HasImage)>();
            using (var read = connection.CreateCommand())
            {
                read.CommandText = """
                    SELECT "Id", "PlainText", "HasImage" FROM "ChatMessages" WHERE "PlainText" LIKE '%<faceType=%'
                    """;
                using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2) == 1));
            }

            foreach (var (id, text, hasImage) in rows)
            {
                var normalized = Mutter.Core.Services.MessageParser.NormalizeFaceTags(text, hasImage);
                if (normalized == text) continue;

                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE \"ChatMessages\" SET \"PlainText\" = $text WHERE \"Id\" = $id";
                var pText = update.CreateParameter();
                pText.ParameterName = "$text";
                pText.Value = normalized;
                var pId = update.CreateParameter();
                pId.ParameterName = "$id";
                pId.Value = id;
                update.Parameters.Add(pText);
                update.Parameters.Add(pId);
                await update.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// 把主键被建成 INTEGER 的表重建为 TEXT 主键（保留原行，键值转为字符串）。
    /// 表名与列名均为调用方写死的内部常量，不接受外部输入，故拼接 DDL 安全。
    /// </summary>
    private async Task RepairIntegerKeyAsync(string table, string pkColumn, CancellationToken ct)
    {
        var connection = Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);

        try
        {
            var ddl = await ScalarTextAsync(connection,
                $"SELECT \"sql\" FROM sqlite_master WHERE type='table' AND name='{table}'", ct);
            // 表还不存在（全新库由 EnsureCreated 按模型建 TEXT 主键）或已是 TEXT → 不动
            if (string.IsNullOrEmpty(ddl) || !ddl.Contains($"\"{pkColumn}\" INTEGER", StringComparison.Ordinal))
                return;

            var columns = new List<(string Name, string Type, bool NotNull, string? Default)>();
            using (var read = connection.CreateCommand())
            {
                read.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    columns.Add((
                        reader.GetString(reader.GetOrdinal("name")),
                        reader.IsDBNull(reader.GetOrdinal("type")) ? "TEXT" : reader.GetString(reader.GetOrdinal("type")),
                        reader.GetInt32(reader.GetOrdinal("notnull")) == 1,
                        reader.IsDBNull(reader.GetOrdinal("dflt_value")) ? null : reader.GetString(reader.GetOrdinal("dflt_value"))));
                }
            }

            var repaired = $"{table}__textkey";
            var definitions = columns.Select(c => c.Name == pkColumn
                ? $"\"{c.Name}\" TEXT NOT NULL CONSTRAINT \"PK_{table}\" PRIMARY KEY"
                : $"\"{c.Name}\" {c.Type}"
                  + (c.NotNull ? " NOT NULL" : string.Empty)
                  + (string.IsNullOrEmpty(c.Default) ? string.Empty : $" DEFAULT {c.Default}"));
            var columnList = string.Join(", ", columns.Select(c => $"\"{c.Name}\""));
            var selectList = string.Join(", ", columns.Select(c => c.Name == pkColumn
                ? $"CAST(\"{c.Name}\" AS TEXT)"
                : $"\"{c.Name}\""));

            using var tx = await connection.BeginTransactionAsync(ct);
            await ExecuteAsync(connection, tx, $"CREATE TABLE \"{repaired}\" ({string.Join(", ", definitions)})", ct);
            await ExecuteAsync(connection, tx,
                $"INSERT INTO \"{repaired}\" ({columnList}) SELECT {selectList} FROM \"{table}\"", ct);
            await ExecuteAsync(connection, tx, $"DROP TABLE \"{table}\"", ct);
            await ExecuteAsync(connection, tx, $"ALTER TABLE \"{repaired}\" RENAME TO \"{table}\"", ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }

        static async Task ExecuteAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx,
            string sql, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        static async Task<string?> ScalarTextAsync(System.Data.Common.DbConnection conn, string sql, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var value = await cmd.ExecuteScalarAsync(ct);
            return value as string ?? (value is System.DBNull or null ? null : value.ToString());
        }
    }
}
