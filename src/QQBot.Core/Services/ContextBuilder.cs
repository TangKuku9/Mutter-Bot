using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Services;

public class ContextBuilder : IContextBuilder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContextBuilder> _logger;

    private const double CharsPerToken = 2.0;
    private const int MinConversationTurns = 4;

    public ContextBuilder(
        IServiceScopeFactory scopeFactory,
        ILogger<ContextBuilder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<BuildContextResult> BuildGroupContextAsync(
        string groupId, string currentMessage, string senderName, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var globalConfig = await configRepo.GetAiConfigAsync(ct) ?? new AiConfig();
        var groupConfig = await configRepo.GetGroupConfigAsync(groupId, ct);

        var effectiveConfig = ApplyGroupOverrides(globalConfig, groupConfig);

        var history = await messageRepo.GetGroupHistoryAsync(
            groupId, effectiveConfig.MaxContextMessages * 2, ct);

        var conversation = BuildConversation(history, currentMessage, senderName, effectiveConfig);

        var (truncatedConversation, estimatedTokens, wasTruncated) =
            TruncateToBudget(conversation, effectiveConfig.MaxTokens);

        _logger.LogDebug("Group {GroupId} context built: {Count} msgs, ~{Tokens} tokens, truncated={Truncated}",
            groupId, truncatedConversation.Count, estimatedTokens, wasTruncated);

        string? characterPrompt = null;
        if (groupConfig?.ActiveCharacter != null)
        {
            var character = AiCharacter.GetDefaults().FirstOrDefault(c => c.Name == groupConfig.ActiveCharacter);
            if (character != null && !string.IsNullOrEmpty(character.SystemPromptOverride))
                characterPrompt = character.SystemPromptOverride;
        }

        return new BuildContextResult
        {
            Messages = truncatedConversation,
            EffectiveConfig = effectiveConfig,
            EstimatedTokens = estimatedTokens,
            MessageCount = truncatedConversation.Count,
            WasTruncated = wasTruncated,
            CharacterPrompt = characterPrompt
        };
    }

    public async Task<BuildContextResult> BuildPrivateContextAsync(
        string userId, string currentMessage, string senderName = "User", CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var config = await configRepo.GetAiConfigAsync(ct) ?? new AiConfig();

        var history = await messageRepo.GetPrivateHistoryAsync(
            userId, config.MaxContextMessages * 2, ct);

        var conversation = BuildConversation(history, currentMessage, senderName, config);

        var (truncatedConversation, estimatedTokens, wasTruncated) =
            TruncateToBudget(conversation, config.MaxTokens);

        _logger.LogDebug("Private {UserId} context built: {Count} msgs, ~{Tokens} tokens",
            userId, truncatedConversation.Count, estimatedTokens);

        return new BuildContextResult
        {
            Messages = truncatedConversation,
            EffectiveConfig = config,
            EstimatedTokens = estimatedTokens,
            MessageCount = truncatedConversation.Count,
            WasTruncated = wasTruncated
        };
    }

    private List<(string Role, string Content)> BuildConversation(
        List<ChatMessage> history, string currentMessage, string senderName, AiConfig config)
    {
        var conversation = new List<(string Role, string Content)>();

        foreach (var msg in history)
        {
            if (string.IsNullOrWhiteSpace(msg.PlainText))
                continue;

            var text = msg.PlainText.Replace("[Image]", "").Replace("[Voice]", "")
                .Replace("[Video]", "").Trim();
            if (string.IsNullOrWhiteSpace(text) && !msg.PlainText.Contains("[@Bot]"))
                continue;

            if (msg.IsBotMessage)
            {
                // 跳过游戏系统回复（非AI生成），避免AI模仿系统文本风格而脱离角色
                if (msg.AiModelUsed is null)
                    continue;
                conversation.Add(("assistant", msg.PlainText));
            }
            else
            {
                conversation.Add(("user", $"[{msg.UserName}]: {msg.PlainText}"));
            }
        }

        if (conversation.Count > config.MaxContextMessages)
        {
            conversation = conversation.TakeLast(config.MaxContextMessages).ToList();
        }

        conversation.Add(("user", $"[{senderName}]: {currentMessage}"));

        return conversation;
    }

    private (List<(string Role, string Content)> Messages, int EstimatedTokens, bool WasTruncated)
        TruncateToBudget(List<(string Role, string Content)> conversation, int maxTokens)
    {
        var tokenBudget = (int)(maxTokens * 0.75);

        var totalChars = conversation.Sum(m => m.Content.Length);
        var estimatedTokens = (int)(totalChars / CharsPerToken);

        if (estimatedTokens <= tokenBudget)
            return (conversation, estimatedTokens, false);

        var minMessages = MinConversationTurns * 2;
        var truncated = new List<(string Role, string Content)>(conversation);

        while (truncated.Count > minMessages)
        {
            totalChars = truncated.Sum(m => m.Content.Length);
            estimatedTokens = (int)(totalChars / CharsPerToken);

            if (estimatedTokens <= tokenBudget)
                return (truncated, estimatedTokens, true);

            truncated.RemoveAt(0);
        }

        totalChars = truncated.Sum(m => m.Content.Length);
        estimatedTokens = (int)(totalChars / CharsPerToken);

        return (truncated, estimatedTokens, true);
    }

    private static AiConfig ApplyGroupOverrides(AiConfig globalConfig, GroupConfig? groupConfig)
    {
        if (groupConfig == null) return globalConfig;

        return new AiConfig
        {
            ApiBaseUrl = globalConfig.ApiBaseUrl,
            ApiKey = globalConfig.ApiKey,
            ModelName = globalConfig.ModelName,
            SystemPrompt = groupConfig.CustomSystemPrompt ?? globalConfig.SystemPrompt,
            Personality = globalConfig.Personality,
            Temperature = groupConfig.CustomTemperature ?? globalConfig.Temperature,
            MaxTokens = globalConfig.MaxTokens,
            MaxContextMessages = globalConfig.MaxContextMessages
        };
    }

}
