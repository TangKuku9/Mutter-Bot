using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mutter.Core.Interfaces;
using Mutter.Core.Models;

namespace Mutter.Core.Services;

/// <summary>正式模型调用和管理员手动探测共用的 HTTP 元数据记账。</summary>
public static class AiHttpAudit
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient httpClient, HttpRequestMessage request,
        string model, string transport, CancellationToken ct, IAiCallStore? callStore, ILogger logger,
        TimeProvider? clock = null, string? purpose = null, string? stage = null)
    {
        var operation = AiCallScope.Current;
        var metadata = operation?.Metadata ?? new();
        var time = clock ?? TimeProvider.System;
        var call = new AiCallRecord
        {
            OperationId = operation?.OperationId ?? Guid.NewGuid().ToString("N"),
            TriggerMessageId = metadata.TriggerMessageId, GroupId = metadata.GroupId,
            RequesterId = metadata.RequesterId, RequesterName = metadata.RequesterName,
            Purpose = purpose ?? operation?.Purpose ?? "chat", Stage = stage ?? operation?.Stage ?? "generate",
            Model = model, Transport = transport, StartedAt = time.GetUtcNow().UtcDateTime
        };
        RuntimeActivity.Current?.Mark(call.Stage, model);
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.SendAsync(request, ct);
            call.HttpStatus = (int)response.StatusCode;
            call.State = response.IsSuccessStatusCode ? "success" : "http_error";
            var raw = await response.Content.ReadAsStringAsync(ct);
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (response.IsSuccessStatusCode && (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(transport == "gemini" ? "candidates" : "choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0))
                    call.State = "invalid_response";
                var usageKey = transport == "gemini" ? "usageMetadata" : "usage";
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(usageKey, out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    call.PromptTokens = ReadCount(usage, transport == "gemini" ? "promptTokenCount" : "prompt_tokens");
                    call.CompletionTokens = ReadCount(usage, transport == "gemini" ? "candidatesTokenCount" : "completion_tokens");
                }
            }
            catch (JsonException) { if (response.IsSuccessStatusCode) call.State = "invalid_response"; }
            return response;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            // ct 通常是 AiCallScope 的 120 秒链接令牌；只有宿主/队列令牌取消才算用户取消，
            // 否则是生成期限到达，应记为 timeout。
            call.State = operation is not null
                ? operation.HostToken.IsCancellationRequested ? "cancelled" : "timeout"
                : ct.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        catch
        {
            response?.Dispose();
            call.State = "network_error";
            throw;
        }
        finally
        {
            call.CompletedAt = time.GetUtcNow().UtcDateTime;
            call.ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (callStore is not null)
            {
                using var receipt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await callStore.SaveAsync(call, receipt.Token); }
                catch (Exception ex) { logger.LogError(ex, "AI usage receipt failed operation={Operation}", call.OperationId); }
            }
        }
    }

    private static long? ReadCount(JsonElement usage, string key) =>
        usage.TryGetProperty(key, out var count) && count.ValueKind == JsonValueKind.Number
        && count.TryGetInt64(out var value) && value >= 0 ? value : null;

}
