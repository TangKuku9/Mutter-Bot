using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Ai.Dtos;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Interfaces;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Game.Ai;

/// <summary>
/// AI 事件生成器。低频（每群每 12-24h）生成群级奇遇/劫难事件，结构化 JSON 存入 GroupWorld。
/// AI 只产出题材/叙事/选项/难度，具体奖励数值由 ExploreEngine/EventApplier 按 difficulty×境界缩放确定。
/// 失败时由调用方走预设模板兜底。
/// </summary>
public class EventGenerator
{
    private readonly IStructuredAiService _structured;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventGenerator> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public EventGenerator(IStructuredAiService structured, IServiceScopeFactory scopeFactory, ILogger<EventGenerator> logger)
    {
        _structured = structured;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>尝试为群生成一个事件。返回 null 表示 AI 未配置或失败（调用方兜底）。</summary>
    public async Task<GeneratedEventDto?> GenerateAsync(string groupId, CancellationToken ct = default)
    {
        if (!await _structured.IsAiConfiguredAsync(ct))
        {
            _logger.LogDebug("AI 未配置，跳过事件生成 group={GroupId}", groupId);
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICultivationRepository>();
        var world = await repo.GetOrCreateGroupWorldAsync(groupId, ct);
        var lb = await repo.GetGroupLeaderboardAsync(groupId, 20, ct);
        var realmDist = lb.Count == 0
            ? "尚无修士"
            : string.Join("、", lb.GroupBy(e => Realm.NameOf(e.RealmIndex))
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}×{g.Count()}"));

        using var cfgScope = _scopeFactory.CreateScope();
        var cfgRepo = cfgScope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var aiConfig = await cfgRepo.GetAiConfigAsync(ct) ?? new AiConfig
        {
            Temperature = 0.9,
            MaxTokens = 400
        };
        aiConfig.Temperature = 0.9;
        aiConfig.MaxTokens = 400;

        var systemPrompt = AiGamePrompts.EventGeneratorSystem(world.WorldQiLevel, realmDist);
        var userPrompt = "生成一个修仙事件。";

        try
        {
            var result = await _structured.GetStructuredAsync<GeneratedEventDto>(systemPrompt, userPrompt, aiConfig, ct);
            if (result is null) return null;
            var dto = result.Data;

            // 钳制：difficulty 范围、清理空白
            dto.Difficulty = AiGamePrompts.ClampDifficulty(dto.Difficulty);
            if (string.IsNullOrWhiteSpace(dto.Title)) dto.Title = "天地异象";
            if (string.IsNullOrWhiteSpace(dto.Narrative)) dto.Narrative = "一股天地灵气在涌动……";
            if (dto.Options.Count == 0)
                dto.Options.Add(new GeneratedEventOptionDto { Label = "上前探查", Risk = "mid" });

            return dto;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "事件生成失败 group={GroupId}", groupId);
            return null;
        }
    }

    /// <summary>把生成的事件序列化为 JSON 存入群世界状态。</summary>
    public static string Serialize(GeneratedEventDto dto) =>
        JsonSerializer.Serialize(dto, JsonOpts);

    public static GeneratedEventDto? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GeneratedEventDto>(json, JsonOpts);
}
