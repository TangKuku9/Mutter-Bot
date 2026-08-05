using Microsoft.EntityFrameworkCore;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;
using QQBot.Infrastructure.Data;
using QQBot.Infrastructure.Entities;

namespace QQBot.Infrastructure.Repositories;

public class ConfigRepository : IConfigRepository
{
    private readonly AppDbContext _dbContext;

    public ConfigRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AiConfig?> GetAiConfigAsync(CancellationToken ct = default)
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync(ct);
        if (entity == null) return null;

        return new AiConfig
        {
            ApiBaseUrl = entity.ApiBaseUrl,
            ApiKey = entity.ApiKey,
            ModelName = entity.ModelName,
            SystemPrompt = entity.SystemPrompt,
            Personality = entity.Personality,
            Temperature = entity.Temperature,
            MaxTokens = entity.MaxTokens,
            MaxContextMessages = entity.MaxContextMessages,
            WebSearchEnabled = entity.WebSearchEnabled
        };
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
            Keywords = string.IsNullOrEmpty(entity.Keywords)
                ? null
                : entity.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            ProactiveKeywords = entity.ProactiveKeywords,
            ProactiveEnabled = entity.ProactiveEnabled,
            ActiveCharacter = entity.ActiveCharacter
        };
    }

    public async Task SaveAiConfigAsync(AiConfig config, CancellationToken ct = default)
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            entity = new AiConfigEntity
            {
                ApiBaseUrl = config.ApiBaseUrl,
                ApiKey = config.ApiKey,
                ModelName = config.ModelName,
                SystemPrompt = config.SystemPrompt,
                Personality = config.Personality,
                Temperature = config.Temperature,
                MaxTokens = config.MaxTokens,
                MaxContextMessages = config.MaxContextMessages,
                WebSearchEnabled = config.WebSearchEnabled
            };
            _dbContext.AiConfigs.Add(entity);
        }
        else
        {
            entity.ApiBaseUrl = config.ApiBaseUrl;
            entity.ApiKey = config.ApiKey;
            entity.ModelName = config.ModelName;
            entity.SystemPrompt = config.SystemPrompt;
            entity.Personality = config.Personality;
            entity.Temperature = config.Temperature;
            entity.MaxTokens = config.MaxTokens;
            entity.MaxContextMessages = config.MaxContextMessages;
            entity.WebSearchEnabled = config.WebSearchEnabled;
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
                Keywords = config.Keywords != null ? string.Join(',', config.Keywords) : null,
                ProactiveKeywords = config.ProactiveKeywords,
                ProactiveEnabled = config.ProactiveEnabled,
                ActiveCharacter = config.ActiveCharacter
            };
            _dbContext.GroupConfigs.Add(entity);
        }
        else
        {
            entity.IsEnabled = config.IsEnabled;
            entity.TriggerMode = config.TriggerMode;
            entity.CustomSystemPrompt = config.CustomSystemPrompt;
            entity.CustomTemperature = config.CustomTemperature;
            entity.Keywords = config.Keywords != null ? string.Join(',', config.Keywords) : null;
            entity.ProactiveKeywords = config.ProactiveKeywords;
            entity.ProactiveEnabled = config.ProactiveEnabled;
            entity.ActiveCharacter = config.ActiveCharacter;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<List<GroupConfig>> GetAllGroupConfigsAsync(CancellationToken ct = default)
    {
        return await _dbContext.GroupConfigs
            .Select(e => new GroupConfig
            {
                GroupId = e.GroupId,
                IsEnabled = e.IsEnabled,
                TriggerMode = e.TriggerMode,
                CustomSystemPrompt = e.CustomSystemPrompt,
                CustomTemperature = e.CustomTemperature,
                Keywords = string.IsNullOrEmpty(e.Keywords)
                    ? null
                    : e.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                ProactiveKeywords = e.ProactiveKeywords,
                ProactiveEnabled = e.ProactiveEnabled,
                ActiveCharacter = e.ActiveCharacter
            })
            .ToListAsync(ct);
    }

    public async Task<bool> IsPrivateChatEnabledAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _dbContext.PrivateChatConfigs
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        // 无记录时默认启用（全新数据库兜底；用户可通过 /private off 显式关闭）
        return entity?.IsEnabled ?? true;
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
}
