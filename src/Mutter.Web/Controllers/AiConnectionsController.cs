using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Web.Models;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/config/ai/connections")]
public sealed class AiConnectionsController(AppDbContext db, IHttpClientFactory clients, IAiModelExecutor executor,
    AiSearchCapabilityStore? capabilities = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AiConnectionDto>>> List(CancellationToken ct) =>
        Ok(await BuildListAsync(db, ct));

    public static async Task<List<AiConnectionDto>> BuildListAsync(AppDbContext db, CancellationToken ct = default)
    {
        var currentId = await db.AiConfigs.Select(c => c.ProviderConnectionId).FirstOrDefaultAsync(ct);
        var presets = await db.AiPresets.Select(p => p.ConfigJson).ToListAsync(ct);
        var references = presets.Select(ReadConnectionId).Where(id => id.Length > 0)
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return (await db.AiProviderConnections.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync(ct))
            .Select(c => ToDto(c, c.Id == currentId, references.GetValueOrDefault(c.Id))).ToList();
    }

    private static string ReadConnectionId(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.TryGetProperty("providerConnectionId", out var id)
                && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    private static AiConnectionDto ToDto(AiProviderConnectionEntity c, bool current = false, int presetCount = 0) => new()
    {
        Id = c.Id, Name = c.Name, TemplateId = c.TemplateId, Protocol = c.Protocol,
        ApiBaseUrl = c.ApiBaseUrl, ApiKey = c.ApiKey.Length > 0 ? AiConnectionStore.SecretMask : "",
        HasApiKey = c.ApiKey.Length > 0, Headers = AiConnectionStore.Headers(c).ToDictionary(
            h => h.Key, _ => AiConnectionStore.SecretMask), TimeoutSeconds = c.TimeoutSeconds,
        DefaultModel = c.DefaultModel, Models = AiConnectionStore.Models(c), IsCurrent = current,
        PresetCount = presetCount, UpdatedAt = c.UpdatedAt, Revision = c.Revision
    };

    [HttpPost]
    public async Task<ActionResult<AiConnectionDto>> Create(AiConnectionInputDto dto, CancellationToken ct)
    {
        if (await db.AiProviderConnections.CountAsync(ct) >= 100)
            return BadRequest(new { error = "最多保存 100 条服务商连接" });
        dto.Id = null;
        var (connection, error) = await PrepareAsync(dto, true, ct);
        if (error != null) return BadRequest(new { error });
        db.AiProviderConnections.Add(connection!);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(connection!));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<AiConnectionDto>> Update(string id, AiConnectionInputDto dto, CancellationToken ct)
    {
        dto.Id = id;
        var (connection, error) = await PrepareAsync(dto, true, ct);
        if (error != null) return BadRequest(new { error });
        var stored = await db.AiProviderConnections.FindAsync([id], ct);
        if (stored == null) return NotFound(new { error = "连接已不存在，请刷新后重试" });
        // 删除模型前检查当前配置与预设，避免产生悬空引用。
        var modelIds = AiConnectionStore.Models(connection!).Select(m => m.Id).ToHashSet();
        var current = await db.AiConfigs.FirstOrDefaultAsync(c => c.ProviderConnectionId == id, ct);
        var referenced = (await db.AiPresets.ToListAsync(ct)).Where(p => ReadConnectionId(p.ConfigJson) == id);
        foreach (var preset in referenced)
        {
            var config = JsonSerializer.Deserialize<AiPresetConfigDto>(preset.ConfigJson, AiConnectionStore.JsonOptions);
            if (config != null && !modelIds.Contains(config.ModelName))
                return Conflict(new { error = $"模型仍被预设「{preset.Name}」使用，请先修改该预设" });
        }
        if (current != null && !modelIds.Contains(current.ModelName))
            return Conflict(new { error = "模型正在使用，请先在主配置中切换模型" });
        var changed = stored.ApiBaseUrl != connection!.ApiBaseUrl || stored.ApiKey != connection.ApiKey
            || stored.Protocol != connection.Protocol || stored.HeadersJson != connection.HeadersJson;
        connection.Revision = changed ? Guid.NewGuid().ToString("N") : stored.Revision;
        db.Entry(stored).CurrentValues.SetValues(connection);
        if (changed) capabilities?.ClearConnection(id);
        if (current != null)
        {
            current.ApiBaseUrl = connection.ApiBaseUrl;
            current.ApiKey = connection.ApiKey;
            if (changed)
            {
                current.WebSearchEndpoint = "";
                current.WebSearchShape = "";
                current.WebSearchVerifiedAt = null;
                current.WebSearchImageCapable = null;
                current.WebSearchImageVerifiedAt = null;
            }
        }
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(stored));
    }

    [HttpPost("{id}/duplicate")]
    public async Task<ActionResult<AiConnectionDto>> Duplicate(string id, CancellationToken ct)
    {
        var source = await db.AiProviderConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (source == null) return NotFound(new { error = "连接已不存在" });
        if (await db.AiProviderConnections.CountAsync(ct) >= 100)
            return BadRequest(new { error = "最多保存 100 条服务商连接" });
        source.Id = Guid.NewGuid().ToString("N");
        source.Revision = Guid.NewGuid().ToString("N");
        source.Name = source.Name.Length > 59 ? source.Name[..59] + " · 副本" : source.Name + " · 副本";
        source.CreatedAt = source.UpdatedAt = DateTime.UtcNow;
        db.AiProviderConnections.Add(source);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(source));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var connection = await db.AiProviderConnections.FindAsync([id], ct);
        if (connection == null) return NotFound();
        if (await db.AiConfigs.AnyAsync(c => c.ProviderConnectionId == id, ct))
            return Conflict(new { error = "该连接正在使用，请先切换并保存当前 AI 配置" });
        if ((await db.AiPresets.Select(p => p.ConfigJson).ToListAsync(ct)).Any(p => ReadConnectionId(p) == id))
            return Conflict(new { error = "该连接仍被预设引用，请先修改或删除对应预设" });
        db.AiProviderConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("discover")]
    public async Task<ActionResult<List<AiConnectionModel>>> Discover(AiConnectionInputDto dto, CancellationToken ct)
    {
        var (connection, error) = await PrepareAsync(dto, false, ct);
        if (error != null) return BadRequest(new { error });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(connection!.TimeoutSeconds, 30)));
        try
        {
            var url = connection.Protocol == "gemini"
                ? $"{connection.ApiBaseUrl}/models?pageSize=1000" : $"{connection.ApiBaseUrl}/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetHeaders(request, connection);
            using var response = await clients.CreateClient("ai-connections").SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { error = $"获取模型列表失败（HTTP {(int)response.StatusCode}），已有模型已保留，可手动添加" });
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var key = connection.Protocol == "gemini" ? "models" : "data";
            if (!data.RootElement.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                return BadRequest(new { error = "接口未返回可识别的模型列表，请手动添加模型" });
            var models = new List<AiConnectionModel>();
            foreach (var model in array.EnumerateArray().Take(2000))
            {
                var idKey = connection.Protocol == "gemini" ? "name" : "id";
                if (!model.TryGetProperty(idKey, out var idNode) || idNode.ValueKind != JsonValueKind.String) continue;
                var id = idNode.GetString() ?? "";
                if (connection.Protocol == "gemini" && id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
                if (id.Length is 0 or > 256 || models.Any(m => m.Id == id)) continue;
                models.Add(new AiConnectionModel { Id = id, Name = id, IsManual = false });
            }
            if (models.Count == 0) return BadRequest(new { error = "接口未返回模型，可手动添加准确的模型 ID" });
            return Ok(models);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return StatusCode(502, new { error = "获取模型列表失败或超时，已有模型已保留，可手动添加" });
        }
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(AiConnectionInputDto dto, CancellationToken ct)
    {
        var (connection, error) = await PrepareAsync(dto, true, ct);
        if (error != null) return BadRequest(new { error });
        var config = new AiConfig { ModelName = connection!.DefaultModel, MaxTokens = 1024, MaxInputTokens = 512 };
        AiConnectionStore.Apply(config, connection);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(connection.TimeoutSeconds, 30)));
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await executor.ExecuteAsync(new AiChatRequest("Reply briefly.",
                [AiMessage.Plain("user", "Reply with OK.")], Search: AiSearchIntent.Off), config,
                AiSearchStrategy.CompatNoSearch, deadline.Token);
            if (string.IsNullOrWhiteSpace(result.Content))
                return StatusCode(502, new { error = "接口未返回模型回复，请检查模型 ID 与接口协议" });
            return Ok(new { message = "模型调用成功", model = config.ModelName,
                elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.PromptTokens, result.CompletionTokens });
        }
        catch (Exception ex) when (ex is AiRequestException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return StatusCode(502, new { error = ex is AiRequestException ai
                ? $"模型调用失败（HTTP {ai.StatusCode}），请检查模型 ID、协议及账号权限"
                : "模型调用失败或超时，请检查地址、密钥、模型及网络" });
        }
    }

    private static void SetHeaders(HttpRequestMessage request, AiProviderConnectionEntity connection)
    {
        if (connection.ApiKey.Length > 0)
            request.Headers.TryAddWithoutValidation(connection.Protocol == "gemini" ? "x-goog-api-key" : "Authorization",
                connection.Protocol == "gemini" ? connection.ApiKey : $"Bearer {connection.ApiKey}");
        foreach (var (name, value) in AiConnectionStore.Headers(connection))
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private async Task<(AiProviderConnectionEntity? Connection, string? Error)> PrepareAsync(
        AiConnectionInputDto dto, bool requireModels, CancellationToken ct)
    {
        var saved = string.IsNullOrEmpty(dto.Id) ? null
            : await db.AiProviderConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == dto.Id, ct);
        if (!string.IsNullOrEmpty(dto.Id) && saved == null) return (null, "连接已不存在，请重新读取");
        var name = (dto.Name ?? "").Trim();
        if (name.Length is 0 or > 64) return (null, "连接名称需要 1–64 个字符");
        if (dto.Protocol is not ("openai" or "gemini")) return (null, "请选择 OpenAI 兼容或 Gemini 原生协议");
        var url = AiConnectionStore.NormalizeUrl(dto.ApiBaseUrl ?? "");
        if (url.Length > 2048 || !AiConnectionStore.IsValidUrl(url)) return (null, "请填写有效的 HTTP(S) 接口根地址，不包含查询参数");
        if (dto.TimeoutSeconds is < 5 or > 120) return (null, "连接超时需要在 5–120 秒之间");
        var key = dto.ApiKey is null or AiConnectionStore.SecretMask ? saved?.ApiKey ?? "" : dto.ApiKey.Trim();
        if (key.Length > 8192 || key.Any(char.IsControl)) return (null, "API Key 格式无效");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oldHeaders = saved == null ? [] : AiConnectionStore.Headers(saved);
        if (dto.Headers == null || dto.Headers.Count > 20) return (null, "最多添加 20 个自定义请求头");
        foreach (var (headerName, headerValue) in dto.Headers)
        {
            if (string.IsNullOrWhiteSpace(headerName) || headerName.Length > 100
                || headerName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                || new[] { "host", "content-length", "transfer-encoding", "connection", "content-type" }.Contains(headerName.ToLowerInvariant()))
                return (null, "自定义请求头名称无效或为受保护字段");
            var value = headerValue == AiConnectionStore.SecretMask ? oldHeaders.GetValueOrDefault(headerName, "") : headerValue ?? "";
            if (value.Length > 8192 || value.Any(char.IsControl)) return (null, "自定义请求头内容无效");
            if (!headers.TryAdd(headerName, value)) return (null, "自定义请求头名称重复");
        }
        var models = dto.Models ?? [];
        if (models.Count > 2000 || (requireModels && models.Count == 0)) return (null, "请添加 1–2000 个模型");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model == null) return (null, "模型内容无效");
            model.Id = (model.Id ?? "").Trim();
            model.Name = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name.Trim();
            if (model.Id.Length is 0 or > 256 || model.Id.Any(char.IsControl) || model.Name.Length > 256 || !ids.Add(model.Id))
                return (null, "模型 ID 不能为空或重复，长度最多 256 个字符");
            if (model.ContextWindowTokens is < 4096 or > 1048576 || model.MaxOutputTokens is < 64 or > 32768)
                return (null, "模型上下文或输出上限超出支持范围");
        }
        var defaultModel = (dto.DefaultModel ?? "").Trim();
        if (requireModels && !ids.Contains(defaultModel)) return (null, "请从模型列表中选择默认模型");
        return (new AiProviderConnectionEntity
        {
            Id = saved?.Id ?? Guid.NewGuid().ToString("N"), Name = name,
            TemplateId = AiProviderPreset.GetDefaults().Any(p => p.Id == dto.TemplateId) ? dto.TemplateId : "custom",
            Protocol = dto.Protocol, ApiBaseUrl = url, ApiKey = key, TimeoutSeconds = dto.TimeoutSeconds,
            HeadersJson = JsonSerializer.Serialize(headers, AiConnectionStore.JsonOptions),
            DefaultModel = defaultModel, ModelsJson = JsonSerializer.Serialize(models, AiConnectionStore.JsonOptions),
            CreatedAt = saved?.CreatedAt ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }, null);
    }
}
