using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Interfaces;

public interface IConfigRepository
{
    Task<AiConfig?> GetAiConfigAsync(CancellationToken ct = default);
    Task<GroupConfig?> GetGroupConfigAsync(string groupId, CancellationToken ct = default);
    Task SaveAiConfigAsync(AiConfig config, CancellationToken ct = default);
    Task SaveGroupConfigAsync(GroupConfig config, CancellationToken ct = default);
    Task<List<GroupConfig>> GetAllGroupConfigsAsync(CancellationToken ct = default);

    /// <summary>择机发言全局配置。无记录时返回带默认值的实例（Enabled=false），调用方不必判空。</summary>
    Task<ProactiveChatConfig> GetProactiveConfigAsync(CancellationToken ct = default);
    Task SaveProactiveConfigAsync(ProactiveChatConfig config, CancellationToken ct = default);
    Task<bool> IsPrivateChatEnabledAsync(string userId, CancellationToken ct = default);
    Task<bool> IsPrivateChatAdminAsync(string userId, CancellationToken ct = default);
    Task SetPrivateChatEnabledAsync(string userId, bool enabled, CancellationToken ct = default);
    Task AddKnownGroupAsync(string groupOpenId, string? name, CancellationToken ct = default);
    Task UpdateKnownGroupNameAsync(string groupOpenId, string name, CancellationToken ct = default);
    Task AddKnownUserAsync(string userOpenId, string? nickname, CancellationToken ct = default);
    Task RemoveKnownGroupAsync(string groupOpenId, CancellationToken ct = default);
    Task<List<GroupInfo>> GetKnownGroupsAsync(CancellationToken ct = default);
    Task<List<FriendInfo>> GetKnownUsersAsync(CancellationToken ct = default);
    Task<List<AiCharacter>> GetCustomCharactersAsync(CancellationToken ct = default);
    Task SaveCustomCharacterAsync(AiCharacter character, CancellationToken ct = default);
    Task<bool> DeleteCustomCharacterAsync(string name, CancellationToken ct = default);
}
