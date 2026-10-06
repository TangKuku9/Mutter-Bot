using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Ai.Dtos;
using QQBot.Core.Game.Engines;
using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;
using QQBot.Core.Interfaces;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Game.Ai;

/// <summary>
/// AI 自由动作裁判。玩家 @bot 发送无法匹配确定性指令的自由文本时调用。
/// AI 输出结构化判决，EventApplier 钳制后应用（cultivationDelta 强制 clamp 到 [-0.3,0.3]）。
/// 失败时返回 null，调用方提示"天道混沌"。
/// </summary>
public class ActionReferee
{
    private readonly IStructuredAiService _structured;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActionReferee> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public ActionReferee(IStructuredAiService structured, IServiceScopeFactory scopeFactory, ILogger<ActionReferee> logger)
    {
        _structured = structured;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>判定玩家自由动作。返回 (判决DTO, 实际应用后的修为变化值)。</summary>
    public async Task<RefereeOutcome?> JudgeAsync(
        PlayerProfile player, string freeText, string? sceneContext, CancellationToken ct = default)
    {
        if (!await _structured.IsAiConfiguredAsync(ct))
            return null;

        using var cfgScope = _scopeFactory.CreateScope();
        var cfgRepo = cfgScope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var aiConfig = await cfgRepo.GetAiConfigAsync(ct) ?? new AiConfig
        {
            Temperature = 0.6,
            MaxTokens = 400
        };
        aiConfig.Temperature = 0.6;
        aiConfig.MaxTokens = 400;

        var systemPrompt = AiGamePrompts.RefereeSystem(
            player.RealmName, player.SpiritRootName, player.TechniqueName, sceneContext);
        var userPrompt = $"玩家动作：\"{freeText}\"";

        try
        {
            var result = await _structured.GetStructuredAsync<RefereeVerdictDto>(systemPrompt, userPrompt, aiConfig, ct);
            if (result is null) return null;
            var dto = result.Data;

            // 钳制：cultivationDelta 强制 [-0.3, 0.3]（防作弊，AI 不可越界）
            var mod = dto.SuggestedModifiers ?? new RefereeModifiersDto();
            var rawDelta = mod.CultivationDelta;
            var clampedDelta = EventApplier.ClampDelta(rawDelta);

            // 一致性校验：feasible=false 时 outcomeType 应为 failure/backfire
            if (!dto.Feasible && dto.OutcomeType is "success" or "partial")
                dto.OutcomeType = "failure";

            // 计算实际修为变化：按比例，低修为/零修为玩家使用固定小额保底。
            // 不再按"当前境界阈值"保底——那会使突破清零后每 5 分钟可刷 ±30% 境界阈值的修为（高境界雪球漏洞）
            var effectiveBase = Math.Max(player.CultivationPoints, (double)GameConstants.RefereeMinEffectiveBase);
            var actualDelta = (long)Math.Round(effectiveBase * clampedDelta);

            return new RefereeOutcome(dto, rawDelta, clampedDelta, actualDelta,
                result.Model, result.PromptTokens, result.CompletionTokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "裁判判定失败 player={Member}", player.MemberOpenId);
            return null;
        }
    }

    public record RefereeOutcome(
        RefereeVerdictDto Verdict,
        double RawDelta,       // AI 原始输出（审计用）
        double ClampedDelta,   // 钳制后
        long ActualCultivationDelta, // 应用到存档的实际值
        string? AiModelUsed,
        int PromptTokens,
        int CompletionTokens);
}
