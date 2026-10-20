using Mutter.Core.Models.Config;

namespace Mutter.Core.Services;

/// <summary>一次搜索尝试走哪条传输通道。Shape 为空表示这次不检索。</summary>
public sealed record AiSearchStrategy(string Endpoint, string? Shape)
{
    public static readonly AiSearchStrategy CompatNoSearch = new("openai", null);
    public bool WithSearch => !string.IsNullOrEmpty(Shape);
}

/// <summary>OpenAI 兼容 /chat/completions 的请求体构建。图片统一转 data URI，避免依赖上游回源 QQ 临时图链。</summary>
public static class OpenAiRequestBuilder
{
    public static Dictionary<string, object> Build(
        AiChatRequest request, AiConfig config, string? searchShape)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var message in request.Messages)
        {
            if (message.Images.Count == 0)
            {
                messages.Add(new { role = message.Role, content = message.Text });
                continue;
            }

            var parts = new List<object>();
            if (!string.IsNullOrEmpty(message.Text))
                parts.Add(new { type = "text", text = message.Text });

            foreach (var image in message.Images)
            {
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data)}"
                    }
                });
            }

            messages.Add(new { role = message.Role, content = parts });
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = config.ModelName,
            ["messages"] = messages,
            ["temperature"] = config.Temperature,
            ["max_tokens"] = config.MaxTokens
        };

        if (!string.IsNullOrEmpty(request.ResponseFormat))
        {
            body["response_format"] = new Dictionary<string, object>
            {
                ["type"] = request.ResponseFormat
            };
        }

        if (!string.IsNullOrEmpty(searchShape))
            WebSearchProbe.ApplyShapeToBody(body, searchShape);

        return body;
    }
}

/// <summary>
/// Gemini 原生 :generateContent 的请求体构建。
/// 图片走 inline_data，和 tools:[{google_search}] 放在同一个请求里——这是图文单次联网的关键。
/// </summary>
public static class GeminiNativeRequestBuilder
{
    public static bool TryBuild(
        AiChatRequest request, AiConfig config, bool withSearch,
        out Dictionary<string, object> body, out string? reason)
    {
        body = new Dictionary<string, object>();
        reason = null;

        if (request.ResponseFormat is not null)
        {
            reason = "结构化输出无法无损映射到原生通道";
            return false;
        }

        var contents = new List<object>();
        var systemParts = new List<string>();

        foreach (var message in request.Messages)
        {
            if (message.Role == "system")
            {
                if (message.Images.Count > 0)
                {
                    reason = "系统消息里带有图片";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(message.Text)) systemParts.Add(message.Text);
                continue;
            }

            if (message.Role is not ("user" or "assistant"))
            {
                reason = $"角色 {message.Role} 无法映射到 Gemini";
                return false;
            }

            // Gemini 的图片输入放在 user 轮；历史 assistant 轮里出现图片时退回兼容接口，别赌上游行为。
            if (message.Images.Count > 0 && message.Role != "user")
            {
                reason = "assistant 历史里带有图片";
                return false;
            }

            var parts = new List<object>();
            if (!string.IsNullOrEmpty(message.Text))
                parts.Add(new Dictionary<string, object> { ["text"] = message.Text });

            foreach (var image in message.Images)
            {
                parts.Add(new Dictionary<string, object>
                {
                    ["inline_data"] = new Dictionary<string, object>
                    {
                        ["mime_type"] = image.MimeType,
                        ["data"] = Convert.ToBase64String(image.Data)
                    }
                });
            }

            if (parts.Count == 0) continue;

            contents.Add(new Dictionary<string, object>
            {
                ["role"] = message.Role == "assistant" ? "model" : "user",
                ["parts"] = parts
            });
        }

        if (contents.Count == 0)
        {
            reason = "没有可发送的消息";
            return false;
        }

        body["contents"] = contents;
        body["generationConfig"] = new Dictionary<string, object>
        {
            ["temperature"] = config.Temperature,
            ["maxOutputTokens"] = config.MaxTokens
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt) || systemParts.Count > 0)
        {
            var text = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? string.Join("\n\n", systemParts)
                : request.SystemPrompt + (systemParts.Count == 0 ? string.Empty : "\n\n" + string.Join("\n\n", systemParts));
            body["systemInstruction"] = new Dictionary<string, object>
            {
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = text } }
            };
        }

        if (withSearch)
        {
            body["tools"] = new List<object>
            {
                new Dictionary<string, object> { ["google_search"] = new Dictionary<string, object>() }
            };
        }

        return true;
    }
}
