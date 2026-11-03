using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Mutter.Infrastructure.Ai;

public sealed class AiModelExecutor(HttpModelTransport transport, ILogger<AiModelExecutor> logger) : IAiModelExecutor
{
    public async Task<AiChatResponse> ExecuteAsync(
        AiChatRequest request, AiConfig config, AiSearchStrategy strategy, CancellationToken ct)
    {
        config = config with { };
        request = AiRequestBudget.Fit(request, config);
        try
        {
            return await InvokeAsync(request, config, strategy, ct);
        }
        catch (AiRequestException ex) when (strategy.Endpoint == "gemini" && !strategy.WithSearch
                                           && ex.IsDeterministicRejection)
        {
            return await InvokeAsync(request, config, AiSearchStrategy.CompatNoSearch, ct);
        }
    }

    private async Task<AiChatResponse> InvokeAsync(
        AiChatRequest request, AiConfig config, AiSearchStrategy strategy, CancellationToken ct)
    {
        IChatClient provider = strategy.Endpoint == "gemini"
            ? new GeminiChatClient(transport, config, strategy.WithSearch)
            : new OpenAiCompatibleChatClient(transport, config, strategy.Shape);
        using var pipeline = new ChatClientBuilder(provider)
            .Use(inner => new AttemptLoggingClient(inner, logger)).Build();
        var messages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        messages.AddRange(request.Messages.Select(m =>
        {
            var content = new List<AIContent> { new TextContent(m.Text) };
            content.AddRange(m.Images.Select(i => new DataContent(i.Data, i.MimeType)));
            return new ChatMessage(new ChatRole(m.Role), content);
        }));
        var response = await pipeline.GetResponseAsync(messages, new ChatOptions
        {
            ModelId = config.ModelName,
            Temperature = (float)config.Temperature,
            MaxOutputTokens = config.MaxTokens,
            ResponseFormat = request.ResponseFormat is null ? null : ChatResponseFormat.Json
        }, ct);
        var result = (AiChatResponse)response.RawRepresentation!;
        if (response.FinishReason == ChatFinishReason.Length) result.FinishReason = "length";
        AiCallScope.Current?.RecordUsage(result.PromptTokens, result.CompletionTokens);
        return result;
    }

    private sealed class AttemptLoggingClient(IChatClient inner, ILogger logger) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                logger.LogInformation("AI attempt purpose={Purpose} model={Model} elapsedMs={Elapsed} input={Input} output={Output} attempt={Attempt}",
                    AiCallScope.Current?.Purpose,
                    response.ModelId, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, AiCallScope.Current?.Attempts);
                return response;
            }
            catch (Exception ex)
            {
                logger.LogWarning("AI attempt failed purpose={Purpose} model={Model} elapsedMs={Elapsed} kind={Kind} attempt={Attempt}",
                    AiCallScope.Current?.Purpose,
                    options?.ModelId, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ex.GetType().Name, AiCallScope.Current?.Attempts);
                throw;
            }
        }
    }
}
