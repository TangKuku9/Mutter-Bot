using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

/// <summary>官方模型的参数约束；只调整请求，不改写用户保存的参数。</summary>
public static class AiModelRequestCompatibility
{
    /// <summary>
    /// 聚合平台模板：模型 ID 带厂商前缀，规则按模型家族套用，与直连官方端点保持一致。
    /// 未知厂商保持模板 ID，不猜。
    /// </summary>
    private static readonly string[] AggregatorTemplates = ["openrouter", "together", "siliconflow", "groq"];

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

        var model = config.ModelName;
        // 厂商前段按“倒数第二段”取：聚合平台可能带分组前缀（Pro/moonshotai/Kimi-…），
        // 直接取最后一个斜杠前的全部内容会把分组也算进厂商名，导致规则失效。
        var segments = model.Split('/');
        var bareModel = segments[^1];
        if (AggregatorTemplates.Contains(provider))
            provider = ModelFamily(segments.Length >= 2 ? segments[^2] : "", bareModel, provider);

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

    /// <summary>
    /// 按模型 ID 的厂商前缀推断家族；无法识别时保留聚合平台模板。
    /// 只推断会真正影响请求能否被接受的家族：OpenAI 推理模型、Kimi、MiniMax。
    /// Claude 的规则是 Anthropic 官方兼容层的怪癖，聚合平台自己会翻译 response_format，因此不套用。
    /// </summary>
    private static string ModelFamily(string vendor, string bareModel, string fallback)
    {
        switch (vendor.ToLowerInvariant())
        {
            case "openai": return "openai";
            case "moonshotai" or "moonshot" or "kimi": return "kimi";
            case "minimaxai" or "minimax": return "minimax";
            case "": break;
            default: return fallback;
        }
        // 无厂商前缀的裸模型名按名称推断（例如自建聚合直接暴露 kimi-k3、gpt-6-luna）。
        if (IsOpenAiReasoningModel(bareModel)) return "openai";
        if (bareModel.StartsWith("kimi-", StringComparison.OrdinalIgnoreCase)) return "kimi";
        if (bareModel.StartsWith("minimax-", StringComparison.OrdinalIgnoreCase)) return "minimax";
        return fallback;
    }

    private static bool IsOpenAiReasoningModel(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
}
