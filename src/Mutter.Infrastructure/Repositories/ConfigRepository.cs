using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Infrastructure.Repositories;

public class ConfigRepository : IConfigRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ConfigRepository>? _logger;

    public ConfigRepository(AppDbContext dbContext, ILogger<ConfigRepository>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<AiConfig?> GetAiConfigAsync(CancellationToken ct = default)
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync(ct);
        if (entity == null) return null;

        // 旧字段没有协议列：按地址与搜索通道推断。否则连接丢失走回退分支时，
        // Gemini 原生连接会被当成 OpenAI 兼容发送，等于回退后必然失败。
        var legacyProtocol = entity.WebSearchEndpoint == "gemini"
            || Uri.TryCreate(entity.ApiBaseUrl, UriKind.Absolute, out var legacyUri)
                && legacyUri.Host == "generativelanguage.googleapis.com"
                    ? "gemini" : "openai";
        var config = new AiConfig
        {
            ProviderConnectionId = entity.ProviderConnectionId,
            Protocol = legacyProtocol,
            ApiBaseUrl = entity.ApiBaseUrl,
            ApiKey = entity.ApiKey,
            ModelName = entity.ModelName,
            SystemPrompt = entity.SystemPrompt,
            Personality = entity.Personality,
            Temperature = entity.Temperature,
            MaxTokens = entity.MaxTokens,
            MaxContextMessages = entity.MaxContextMessages,
            ContextWindowTokens = entity.ContextWindowTokens,
            MaxInputTokens = entity.MaxInputTokens,
            GroupMemoryInjectionEnabled = entity.GroupMemoryInjectionEnabled,
            MaxAttachedImages = entity.MaxAttachedImages,
            WebSearchEnabled = entity.WebSearchEnabled,
            WebSearchEndpoint = entity.WebSearchEndpoint,
            WebSearchShape = entity.WebSearchShape,
            WebSearchVerifiedAt = entity.WebSearchVerifiedAt,
            WebSearchImageMode = entity.WebSearchImageMode,
            WebSearchImageCapable = entity.WebSearchImageCapable,
            WebSearchImageVerifiedAt = entity.WebSearchImageVerifiedAt,
            ExternalSearch = ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson)
        };
        if (entity.ProviderConnectionId.Length > 0)
        {
            var connection = await _dbContext.AiProviderConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == entity.ProviderConnectionId, ct);
            if (connection != null)
            {
                AiConnectionStore.Apply(config, connection);
            }
            else
            {
                // 连接被备份恢复/手工改库/并发删除弄丢时不要让每条消息都抛异常：
                // 回退到旧字段继续服务，并明确留痕，重启时由迁移自愈。
                _logger?.LogWarning(
                    "AI 配置引用的服务商连接已不存在，本次回退旧字段运行 connection={Connection}",
                    entity.ProviderConnectionId);
            }
        }
        return config;
    }

    public async Task<GroupConfig?> GetGroupConfigAsync(string groupId, CancellationToken ct = default)
    {
        var entity = await _dbContext.GroupConfigs
            .FirstOrDefaultAsync(g => g.GroupId == groupId, ct);
        if (entity == null) return null;

        return new GroupConfig
        {
            GroupId = entity.GroupId,
            IsEnabled = entity.IsEnabled,
            TriggerMode = entity.TriggerMode,
            CustomSystemPrompt = entity.CustomSystemPrompt,
            CustomTemperature = entity.CustomTemperature,
            AiProactiveEnabled = entity.AiProactiveEnabled,
            ProactiveOverrides = ProactiveOverridesCodec.Decode(entity.ProactiveOverridesJson),
            BotMentionOpenId = entity.BotMentionOpenId,
            ActiveCharacter = entity.ActiveCharacter,
            MemoryInjectionEnabled = entity.MemoryInjectionEnabled
        };
    }

    public async Task SaveAiConfigAsync(AiConfig config, CancellationToken ct = default)
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            entity = new AiConfigEntity
            {
                ProviderConnectionId = config.ProviderConnectionId,
                ApiBaseUrl = config.ApiBaseUrl,
                ApiKey = config.ApiKey,
                ModelName = config.ModelName,
                SystemPrompt = config.SystemPrompt,
                Personality = config.Personality,
                Temperature = config.Temperature,
                MaxTokens = config.MaxTokens,
                MaxContextMessages = config.MaxContextMessages,
                ContextWindowTokens = config.ContextWindowTokens,
                MaxInputTokens = config.MaxInputTokens,
                GroupMemoryInjectionEnabled = config.GroupMemoryInjectionEnabled,
                MaxAttachedImages = config.MaxAttachedImages,
                WebSearchEnabled = config.WebSearchEnabled,
                WebSearchEndpoint = config.WebSearchEndpoint,
                WebSearchShape = config.WebSearchShape,
                WebSearchVerifiedAt = config.WebSearchVerifiedAt,
                WebSearchImageMode = config.WebSearchImageMode,
                WebSearchImageCapable = config.WebSearchImageCapable,
                WebSearchImageVerifiedAt = config.WebSearchImageVerifiedAt,
                ExternalSearchJson = ExternalSearchJsonCodec.Encode(config.ExternalSearch)
            };
            _dbContext.AiConfigs.Add(entity);
        }
        else
        {
            entity.ProviderConnectionId = config.ProviderConnectionId;
            entity.ApiBaseUrl = config.ApiBaseUrl;
            entity.ApiKey = config.ApiKey;
            entity.ModelName = config.ModelName;
            entity.SystemPrompt = config.SystemPrompt;
            entity.Personality = config.Personality;
            entity.Temperature = config.Temperature;
            entity.MaxTokens = config.MaxTokens;
            entity.MaxContextMessages = config.MaxContextMessages;
            entity.ContextWindowTokens = config.ContextWindowTokens;
            entity.MaxInputTokens = config.MaxInputTokens;
            entity.GroupMemoryInjectionEnabled = config.GroupMemoryInjectionEnabled;
            entity.MaxAttachedImages = config.MaxAttachedImages;
            entity.WebSearchEnabled = config.WebSearchEnabled;
            entity.WebSearchEndpoint = config.WebSearchEndpoint;
            entity.WebSearchShape = config.WebSearchShape;
            entity.WebSearchVerifiedAt = config.WebSearchVerifiedAt;
            entity.WebSearchImageMode = config.WebSearchImageMode;
            entity.WebSearchImageCapable = config.WebSearchImageCapable;
            entity.WebSearchImageVerifiedAt = config.WebSearchImageVerifiedAt;
            entity.ExternalSearchJson = ExternalSearchJsonCodec.Encode(config.ExternalSearch);
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task SaveGroupConfigAsync(GroupConfig config, CancellationToken ct = default)
    {
        var entity = await _dbContext.GroupConfigs
            .FirstOrDefaultAsync(g => g.GroupId == config.GroupId, ct);

        if (entity == null)
        {
            entity = new GroupConfigEntity
            {
                GroupId = config.GroupId,
                IsEnabled = config.IsEnabled,
                TriggerMode = config.TriggerMode,
                CustomSystemPrompt = config.CustomSystemPrompt,
                CustomTemperature = config.CustomTemperature,
                AiProactiveEnabled = config.AiProactiveEnabled,
                ProactiveOverridesJson = EncodeOverrides(config.ProactiveOverrides),
                BotMentionOpenId = config.BotMentionOpenId,
                ActiveCharacter = config.ActiveCharacter,
                MemoryInjectionEnabled = config.MemoryInjectionEnabled
            };
            _dbContext.GroupConfigs.Add(entity);
        }
        else
        {
            entity.IsEnabled = config.IsEnabled;
            entity.TriggerMode = config.TriggerMode;
            entity.CustomSystemPrompt = config.CustomSystemPrompt;
            entity.CustomTemperature = config.CustomTemperature;
            entity.AiProactiveEnabled = config.AiProactiveEnabled;
            // partial 保存契约：ProactiveOverrides 为 null 表示保持原值，不整行覆盖。
            if (config.ProactiveOverrides is not null)
                entity.ProactiveOverridesJson = EncodeOverrides(config.ProactiveOverrides);
            entity.BotMentionOpenId = config.BotMentionOpenId;
            entity.ActiveCharacter = config.ActiveCharacter;
            entity.MemoryInjectionEnabled = config.MemoryInjectionEnabled;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 写入前按当前模型上下文窗口收敛判定输出预算：绕过面板的写路径（自动配置、命令、
    /// 未来调用方）也不会把超限值落库。读取路径不额外查询。
    /// </summary>
    private string EncodeOverrides(ProactiveOverrides? overrides)
    {
        if (overrides is null) return "{}";
        if (overrides.EvalMaxTokens.HasValue)
        {
            var contextWindow = _dbContext.AiConfigs.AsNoTracking().Select(a => (int?)a.ContextWindowTokens).FirstOrDefault()
                ?? AiRequestBudget.DefaultContextWindow;
            overrides.EvalMaxTokens = AiRequestBudget.ClampOutputTokens(overrides.EvalMaxTokens.Value, contextWindow);
        }
        return ProactiveOverridesCodec.Encode(overrides);
    }

    public async Task<List<GroupConfig>> GetAllGroupConfigsAsync(CancellationToken ct = default)
    {
        return (await _dbContext.GroupConfigs.AsNoTracking().ToListAsync(ct))
            .Select(e => new GroupConfig
            {
                GroupId = e.GroupId,
                IsEnabled = e.IsEnabled,
                TriggerMode = e.TriggerMode,
                CustomSystemPrompt = e.CustomSystemPrompt,
                CustomTemperature = e.CustomTemperature,
                AiProactiveEnabled = e.AiProactiveEnabled,
                ProactiveOverrides = ProactiveOverridesCodec.Decode(e.ProactiveOverridesJson),
                BotMentionOpenId = e.BotMentionOpenId,
                ActiveCharacter = e.ActiveCharacter,
                MemoryInjectionEnabled = e.MemoryInjectionEnabled
            })
            .ToList();
    }

    public async Task<bool> IsPrivateChatEnabledAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _dbContext.PrivateChatConfigs
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        // 无记录时默认启用（全新数据库兜底；用户可通过 /private off 显式关闭）
        return entity?.IsEnabled ?? true;
    }

    /// <summary>
    /// 择机发言配置。无行时返回默认值（Enabled=false），读取路径不写库——
    /// 冷启动时多个后台服务同时读，写兜底行会造成并发主键冲突。
    /// </summary>
    public async Task<ProactiveChatConfig> GetProactiveConfigAsync(CancellationToken ct = default)
    {
        var entity = await _dbContext.ProactiveChatConfigs.FirstOrDefaultAsync(ct);
        if (entity is null) return new ProactiveChatConfig().Normalize();

        return new ProactiveChatConfig
        {
            Enabled = entity.Enabled,
            WindowSeconds = entity.WindowSeconds,
            WindowMaxMessages = entity.WindowMaxMessages,
            CooldownMinutes = entity.CooldownMinutes,
            MaxPerGroupPerDay = entity.MaxPerGroupPerDay,
            MaxGlobalPerDay = entity.MaxGlobalPerDay,
            QuietStartHour = entity.QuietStartHour,
            QuietEndHour = entity.QuietEndHour,
            Liveliness = entity.Liveliness,
            MaxAttachedImages = entity.MaxAttachedImages,
            HistoryMaxMessages = entity.HistoryMaxMessages,
            EvalMaxTokens = entity.EvalMaxTokens,
            WebSearchEnabled = entity.WebSearchEnabled,
            AllowActiveChannel = entity.AllowActiveChannel,
            ActivePerGroupPerMinute = entity.ActivePerGroupPerMinute,
            ActivePerGroupPerDay = entity.ActivePerGroupPerDay,
            ActivePerAccountPerMinute = entity.ActivePerAccountPerMinute,
            MemoryInjectionEnabled = entity.MemoryInjectionEnabled,
            UpdatedAt = entity.UpdatedAt
        }.Normalize();
    }

    public async Task SaveProactiveConfigAsync(ProactiveChatConfig config, CancellationToken ct = default)
    {
        var normalized = config.Normalize();
        // 与群级覆盖一致的兜底：面板之外的写路径也不能把超出模型窗口的输出预算落库。
        var contextWindow = await _dbContext.AiConfigs.AsNoTracking()
            .Select(a => (int?)a.ContextWindowTokens).FirstOrDefaultAsync(ct) ?? AiRequestBudget.DefaultContextWindow;
        normalized.EvalMaxTokens = AiRequestBudget.ClampOutputTokens(normalized.EvalMaxTokens, contextWindow);
        var entity = await _dbContext.ProactiveChatConfigs.FirstOrDefaultAsync(e => e.Id == 1, ct);
        if (entity is null)
        {
            entity = new ProactiveChatConfigEntity { Id = 1 };
            _dbContext.ProactiveChatConfigs.Add(entity);
        }

        entity.Enabled = normalized.Enabled;
        entity.WindowSeconds = normalized.WindowSeconds;
        entity.WindowMaxMessages = normalized.WindowMaxMessages;
        entity.CooldownMinutes = normalized.CooldownMinutes;
        entity.MaxPerGroupPerDay = normalized.MaxPerGroupPerDay;
        entity.MaxGlobalPerDay = normalized.MaxGlobalPerDay;
        entity.QuietStartHour = normalized.QuietStartHour;
        entity.QuietEndHour = normalized.QuietEndHour;
        entity.Liveliness = normalized.Liveliness;
        entity.MaxAttachedImages = normalized.MaxAttachedImages;
        entity.HistoryMaxMessages = normalized.HistoryMaxMessages;
        entity.EvalMaxTokens = normalized.EvalMaxTokens;
        entity.WebSearchEnabled = normalized.WebSearchEnabled;
        entity.AllowActiveChannel = normalized.AllowActiveChannel;
        entity.ActivePerGroupPerMinute = normalized.ActivePerGroupPerMinute;
        entity.ActivePerGroupPerDay = normalized.ActivePerGroupPerDay;
        entity.ActivePerAccountPerMinute = normalized.ActivePerAccountPerMinute;
        entity.MemoryInjectionEnabled = normalized.MemoryInjectionEnabled;
        entity.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<bool> IsPrivateChatAdminAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _dbContext.PrivateChatConfigs
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return entity?.IsAdmin ?? false;
    }

    public async Task SetPrivateChatEnabledAsync(string userId, bool enabled, CancellationToken ct = default)
    {
        var entity = await _dbContext.PrivateChatConfigs
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (entity == null)
        {
            _dbContext.PrivateChatConfigs.Add(new PrivateChatConfigEntity
            {
                UserId = userId,
                IsEnabled = enabled
            });
        }
        else
        {
            entity.IsEnabled = enabled;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task AddKnownGroupAsync(string groupOpenId, string? name, CancellationToken ct = default)
    {
        var entity = await _dbContext.KnownGroups
            .FirstOrDefaultAsync(g => g.GroupOpenId == groupOpenId, ct);
        if (entity == null)
        {
            _dbContext.KnownGroups.Add(new KnownGroupEntity
            {
                GroupOpenId = groupOpenId,
                Name = name,
                AddedAt = DateTime.UtcNow
            });
        }
        else
        {
            entity.Name = name;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateKnownGroupNameAsync(string groupOpenId, string name, CancellationToken ct = default)
    {
        var entity = await _dbContext.KnownGroups
            .FirstOrDefaultAsync(g => g.GroupOpenId == groupOpenId, ct);
        if (entity == null)
        {
            _dbContext.KnownGroups.Add(new KnownGroupEntity
            {
                GroupOpenId = groupOpenId,
                Name = name,
                AddedAt = DateTime.UtcNow
            });
        }
        else
        {
            entity.Name = name;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task AddKnownUserAsync(string userOpenId, string? nickname, CancellationToken ct = default)
    {
        var entity = await _dbContext.KnownUsers
            .FirstOrDefaultAsync(u => u.UserOpenId == userOpenId, ct);
        if (entity == null)
        {
            _dbContext.KnownUsers.Add(new KnownUserEntity
            {
                UserOpenId = userOpenId,
                Nickname = nickname,
                AddedAt = DateTime.UtcNow
            });
        }
        else
        {
            entity.Nickname = nickname;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task RemoveKnownGroupAsync(string groupOpenId, CancellationToken ct = default)
    {
        var entity = await _dbContext.KnownGroups
            .FirstOrDefaultAsync(g => g.GroupOpenId == groupOpenId, ct);
        if (entity != null)
        {
            _dbContext.KnownGroups.Remove(entity);
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task<List<GroupInfo>> GetKnownGroupsAsync(CancellationToken ct = default)
    {
        return await _dbContext.KnownGroups
            .Select(e => new GroupInfo
            {
                GroupId = e.GroupOpenId,
                GroupName = e.Name ?? string.Empty
            })
            .ToListAsync(ct);
    }

    public async Task<List<FriendInfo>> GetKnownUsersAsync(CancellationToken ct = default)
    {
        return await _dbContext.KnownUsers
            .Select(e => new FriendInfo
            {
                UserId = e.UserOpenId,
                Nickname = e.Nickname ?? string.Empty
            })
            .ToListAsync(ct);
    }

    public async Task<List<AiCharacter>> GetCustomCharactersAsync(CancellationToken ct = default)
    {
        return await _dbContext.CustomCharacters
            .OrderBy(e => e.CreatedAt)
            .Select(e => new AiCharacter
            {
                Name = e.Name,
                DisplayName = e.DisplayName,
                Description = e.Description,
                SystemPromptOverride = e.SystemPrompt
            })
            .ToListAsync(ct);
    }

    public async Task SaveCustomCharacterAsync(AiCharacter character, CancellationToken ct = default)
    {
        var entity = await _dbContext.CustomCharacters
            .FirstOrDefaultAsync(c => c.Name == character.Name, ct);
        if (entity == null)
        {
            entity = new CustomCharacterEntity
            {
                Name = character.Name,
                DisplayName = character.DisplayName,
                Description = character.Description,
                SystemPrompt = character.SystemPromptOverride,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.CustomCharacters.Add(entity);
        }
        else
        {
            entity.DisplayName = character.DisplayName;
            entity.Description = character.Description;
            entity.SystemPrompt = character.SystemPromptOverride;
            entity.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteCustomCharacterAsync(string name, CancellationToken ct = default)
    {
        var entity = await _dbContext.CustomCharacters
            .FirstOrDefaultAsync(c => c.Name == name, ct);
        if (entity == null) return false;

        _dbContext.CustomCharacters.Remove(entity);
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }
}
