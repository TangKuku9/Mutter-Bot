using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

/// <summary>官方模型的参数约束；只调整请求，不改写用户保存的参数。</summary>
public static class AiModelRequestCompatibility
{
    public static void Apply(Dictionary<string, object> body, AiConfig config)
    {
        var provider = config.ProviderTemplateId;
        if (provider is "" or "custom" && Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute, out var uri))
            provider = uri.Host switch
            {
                "api.openai.com" => "openai",
                "api.moonshot.cn" or "api.moonshot.ai" => "kimi",
                "api.minimax.cn" or "api.minimax.io" or "api.minimaxi.com" => "minimax",
                "api.anthropic.com" => "anthropic",
                _ => provider
            };

        // 参数由接收请求的平台定义。聚合平台保留标准请求，不能按模型原厂名称改写它的契约。
        var bareModel = config.ModelName;

        var openAiReasoning = provider == "openai" && IsOpenAiReasoningModel(bareModel);
        var kimiCurrent = provider == "kimi" && (bareModel.StartsWith("kimi-k3", StringComparison.OrdinalIgnoreCase)
            || bareModel.StartsWith("kimi-k2.5", StringComparison.OrdinalIgnoreCase)
            || bareModel.StartsWith("kimi-k2.6", StringComparison.OrdinalIgnoreCase)
            || bareModel.StartsWith("kimi-k2.7", StringComparison.OrdinalIgnoreCase));
        var minimaxCurrent = provider == "minimax" && bareModel.StartsWith("MiniMax-M3", StringComparison.OrdinalIgnoreCase);

        if (openAiReasoning || kimiCurrent || minimaxCurrent)
        {
            if (body.Remove("max_tokens", out var limit)) body["max_completion_tokens"] = limit;
            // GPT 推理模式不接受自定义采样；Kimi / MiniMax 当前模型使用固定采样值。
            body.Remove("temperature");
        }
        if (minimaxCurrent)
            body["reasoning_split"] = true; // 思考内容独立返回，避免混入聊天正文和 JSON 结果。
        if (provider == "anthropic" && body.TryGetValue("response_format", out var format)
            && format is Dictionary<string, object> parameters
            && parameters.GetValueOrDefault("type") is "json_object")
            body.Remove("response_format"); // 官方兼容层拒绝 json_object，沿用原提示词与结果校验。
    }

    private static bool IsOpenAiReasoningModel(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
}
