using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

/// <summary>
/// 未知模型采用保守的本地预算。字符估算不是 tokenizer，图片额度也需按实际模型校准。
/// 输入上限与输出上限独立；在最终系统词、图片和响应格式都确定后执行。
/// </summary>
public static class AiRequestBudget
{
    public const int DefaultContextWindow = 32768;
    public const int DefaultInputBudget = 16384;
    public const int SafetyTokens = 1024;
    public const int ImageTokens = 4096;

    public static int InputBudget(int outputTokens, int contextWindow = DefaultContextWindow,
        int maxInputTokens = DefaultInputBudget) =>
        Math.Max(0, Math.Min(maxInputTokens, contextWindow - Math.Max(0, outputTokens) - SafetyTokens));

    public static int EstimateText(string text) => text.Length; // 对中英文均取保守估算

    public static int Estimate(AiChatRequest request) =>
        EstimateText(request.SystemPrompt) + 8
        + request.Messages.Sum(m => EstimateText(m.Text) + 8 + m.Images.Count * ImageTokens)
        + (request.ResponseFormat is null ? 0 : 128);

    public static AiChatRequest Fit(AiChatRequest request, AiConfig config)
    {
        var budget = InputBudget(config.MaxTokens, config.ContextWindowTokens, config.MaxInputTokens);
        var messages = request.Messages.ToList();
        var fitted = request with { Messages = messages };
        while (messages.Count > 1 && Estimate(fitted) > budget)
            messages.RemoveAt(0);
        if (Estimate(fitted) > budget)
            throw new AiInputTooLargeException("当前消息、系统提示或图片超出模型输入预算，请缩短内容或减少图片。");
        return fitted;
    }
}

public sealed class AiInputTooLargeException(string message) : Exception(message);
