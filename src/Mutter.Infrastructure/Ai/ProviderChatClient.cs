using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiMessage = Mutter.Core.Services.AiMessage;

namespace Mutter.Infrastructure.Ai;

/// <summary>每个实例绑定不可变配置快照；HTTP 客户端由宿主管理。</summary>
public abstract class ProviderChatClient(AiConfig config) : IChatClient
{
    private readonly AiConfig _configuration = config with { };
    protected abstract Task<AiChatResponse> SendAsync(AiChatRequest request, AiConfig config, CancellationToken ct);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var all = messages.ToList();
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: not null }
            || options?.Tools is { Count: > 0 } || options?.ConversationId is not null)
            throw new NotSupportedException("当前协议适配器支持无状态聊天与 JSON mode；严格 Schema、函数调用和服务端会话需单独适配。");
        foreach (var content in all.SelectMany(m => m.Contents))
            if (content is not TextContent && !(content is DataContent data
                && data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("当前协议适配器仅接受文本和已解析的图片字节。");
        var system = string.Join("\n\n", all.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var body = all.Where(m => m.Role != ChatRole.System).Select(m => new AiMessage(
            m.Role.Value, m.Text,
            m.Contents.OfType<DataContent>().Select(d =>
                new AiResolvedImage(d.MediaType, d.Data.ToArray(), string.Empty)).ToList())).ToList();
        var effective = _configuration with
        {
            ModelName = options?.ModelId ?? _configuration.ModelName,
            Temperature = options?.Temperature ?? _configuration.Temperature,
            MaxTokens = options?.MaxOutputTokens ?? _configuration.MaxTokens
        };
        var format = options?.ResponseFormat is ChatResponseFormatJson ? "json_object" : null;
        var request = AiRequestBudget.Fit(new AiChatRequest(system, body, format), effective);
        var response = await SendAsync(request, effective, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response.Content))
        {
            ModelId = response.Model,
            FinishReason = response.FinishReason is "length" or "MAX_TOKENS"
                ? ChatFinishReason.Length
                : response.FinishReason is null ? null : new ChatFinishReason(response.FinishReason),
            Usage = new UsageDetails
            {
                InputTokenCount = response.PromptTokens,
                OutputTokenCount = response.CompletionTokens,
                TotalTokenCount = response.TotalTokens
            },
            RawRepresentation = response
        };
    }

    // 保留当前非流式协议行为，统一接口返回一次完整更新；不伪造逐 token 流。
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

public sealed class OpenAiCompatibleChatClient(
    HttpModelTransport transport, AiConfig config, string? searchShape) : ProviderChatClient(config)
{
    protected override Task<AiChatResponse> SendAsync(AiChatRequest request, AiConfig config, CancellationToken ct) =>
        transport.SendOpenAiAsync(request, config, searchShape, ct);
}

public sealed class GeminiChatClient(
    HttpModelTransport transport, AiConfig config, bool withSearch) : ProviderChatClient(config)
{
    protected override Task<AiChatResponse> SendAsync(AiChatRequest request, AiConfig config, CancellationToken ct)
    {
        if (!GeminiNativeRequestBuilder.TryBuild(request, config, withSearch, out var body, out var reason))
            throw new AiRequestException(415, reason ?? "请求无法映射到 Gemini");
        return transport.SendGeminiNativeBodyAsync(body, config, ct);
    }
}
