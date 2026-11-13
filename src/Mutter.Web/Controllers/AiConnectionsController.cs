using System.Data;
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
    /// <summary>上游调试请求的进程级节流：防止面板会话被用来批量消耗额度或扫描内网。</summary>
    private const int UpstreamCallsPerMinute = 60;
    private const int UpstreamConcurrency = 2;
    private static readonly SemaphoreSlim UpstreamGate = new(UpstreamConcurrency, UpstreamConcurrency);
    private static readonly object RateLock = new();
    private static DateTime _rateWindowStart = DateTime.MinValue;
    private static int _rateCount;

    private static bool TryAcquireUpstreamBudget(out string? error)
    {
        lock (RateLock)
        {
            var now = DateTime.UtcNow;
            if (now - _rateWindowStart >= TimeSpan.FromMinutes(1))
            {
                _rateWindowStart = now;
                _rateCount = 0;
            }
            if (_rateCount >= UpstreamCallsPerMinute)
            {
                error = "调试请求过于频繁，请稍后再试";
                return false;
            }
            _rateCount++;
        }
        error = null;
        return true;
    }

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
            // 空值保持空串：否则用户无法分辨“这个头有没有值”。
            h => h.Key, h => h.Value.Length > 0 ? AiConnectionStore.SecretMask : ""),
        TimeoutSeconds = c.TimeoutSeconds,
        DefaultModel = c.DefaultModel, Models = AiConnectionStore.Models(c), IsCurrent = current,
        PresetCount = presetCount, UpdatedAt = c.UpdatedAt, Revision = c.Revision
    };

    [HttpPost]
    public async Task<ActionResult<AiConnectionDto>> Create(AiConnectionInputDto dto, CancellationToken ct)
    {
        // BEGIN IMMEDIATE：把“计数 + 插入”串行化，避免并发请求同时读到 99 条突破上限。
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.AiProviderConnections.CountAsync(ct) >= 100)
            return BadRequest(new { error = "最多保存 100 条服务商连接" });
        dto.Id = null;
        var (connection, error) = await PrepareAsync(dto, true, ct);
        if (error != null) return BadRequest(new { error });
        db.AiProviderConnections.Add(connection!);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(ToDto(connection!));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<AiConnectionDto>> Update(string id, AiConnectionInputDto dto, CancellationToken ct)
    {
        dto.Id = id;
        // 先按跟踪方式读出来：UpdatedAt 是并发令牌，EF 会用这次读到的值做 UPDATE 条件。
        // Prepare 再读一次是为了拿到最新快照做打码/模板校验；两个会话同时编辑时，
        // 后提交者会在这里被判为冲突，而不是用旧快照静默覆盖对方（包括把轮换过的 Key 写回去）。
        var stored = await db.AiProviderConnections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (stored == null) return NotFound(new { error = "连接已不存在，请刷新后重试" });
        var (connection, error) = await PrepareAsync(dto, true, ct);
        if (error != null) return BadRequest(new { error });
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // 删除模型前检查当前配置与预设，避免产生悬空引用。
        var modelIds = AiConnectionStore.Models(connection!).Select(m => m.Id).ToHashSet();
        var current = await db.AiConfigs.FirstOrDefaultAsync(c => c.ProviderConnectionId == id, ct);
        var referenced = (await db.AiPresets.ToListAsync(ct))
            .Where(p => ReadConnectionId(p.ConfigJson) == id).ToList();
        foreach (var preset in referenced)
        {
            AiPresetConfigDto? config;
            try
            {
                config = JsonSerializer.Deserialize<AiPresetConfigDto>(preset.ConfigJson, AiConnectionStore.JsonOptions);
            }
            catch (JsonException)
            {
                // 损坏的预设本来就无法应用，不该阻塞连接维护。
                continue;
            }
            if (config != null && !modelIds.Contains(config.ModelName))
                return Conflict(new { error = $"模型仍被预设「{preset.Name}」使用，请先修改该预设" });
        }
        if (current != null && !modelIds.Contains(current.ModelName))
            return Conflict(new { error = "模型正在使用，请先在主配置中切换模型" });
        var changed = stored.ApiBaseUrl != connection!.ApiBaseUrl || stored.ApiKey != connection.ApiKey
            || stored.Protocol != connection.Protocol || stored.TemplateId != connection.TemplateId
            || stored.TimeoutSeconds != connection.TimeoutSeconds
            || !AiConnectionStore.HeadersEqual(stored.HeadersJson, connection.HeadersJson);
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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "连接已被其他会话修改，请刷新后重试" });
        }
        await tx.CommitAsync(ct);
        return Ok(ToDto(stored));
    }

    [HttpPost("{id}/duplicate")]
    public async Task<ActionResult<AiConnectionDto>> Duplicate(string id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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
        await tx.CommitAsync(ct);
        return Ok(ToDto(source));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        // 引用检查与删除必须在同一把写锁里，否则并发“切换到该连接 + 删除”会留下悬空引用。
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var connection = await db.AiProviderConnections.FindAsync([id], ct);
        if (connection == null) return NotFound();
        if (await db.AiConfigs.AnyAsync(c => c.ProviderConnectionId == id, ct))
            return Conflict(new { error = "该连接正在使用，请先切换并保存当前 AI 配置" });
        if ((await db.AiPresets.Select(p => p.ConfigJson).ToListAsync(ct)).Any(p => ReadConnectionId(p) == id))
            return Conflict(new { error = "该连接仍被预设引用，请先修改或删除对应预设" });
        db.AiProviderConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        capabilities?.ClearConnection(id);
        return NoContent();
    }

    [HttpPost("discover")]
    public async Task<ActionResult<List<AiConnectionModel>>> Discover(AiConnectionInputDto dto, CancellationToken ct)
    {
        if (!TryAcquireUpstreamBudget(out var limited)) return StatusCode(429, new { error = limited });
        var (connection, error) = await PrepareAsync(dto, false, ct);
        if (error != null) return BadRequest(new { error });
        if (!await UpstreamGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return StatusCode(429, new { error = "同时进行的调试请求过多，请稍后再试" });
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
                return StatusCode(502, new { error = $"获取模型列表失败：{DescribeUpstreamFailure((int)response.StatusCode)}。已有模型已保留，可手动添加" });
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
                // 与保存路径同一套字符规则：控制字符会破坏面板显示与请求构造。
                if (id.Length is 0 or > 256 || id.Any(char.IsControl) || models.Any(m => m.Id == id)) continue;
                models.Add(new AiConnectionModel { Id = id, Name = id, IsManual = false });
            }
            if (models.Count == 0) return BadRequest(new { error = "接口未返回模型，可手动添加准确的模型 ID" });
            return Ok(models);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return StatusCode(502, new { error = "获取模型列表失败或超时。已有模型已保留，可手动添加" });
        }
        finally
        {
            UpstreamGate.Release();
        }
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(AiConnectionInputDto dto, CancellationToken ct)
    {
        if (!TryAcquireUpstreamBudget(out var limited)) return StatusCode(429, new { error = limited });
        var (connection, error) = await PrepareAsync(dto, true, ct);
        if (error != null) return BadRequest(new { error });
        if (!await UpstreamGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return StatusCode(429, new { error = "同时进行的调试请求过多，请稍后再试" });
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
                ? $"模型调用失败：{DescribeUpstreamFailure(ai.StatusCode)}。请检查模型 ID、协议及账号权限"
                : "模型调用失败或超时，请检查地址、密钥、模型及网络" });
        }
        finally
        {
            UpstreamGate.Release();
        }
    }

    /// <summary>只给粗略原因：逐字回显上游状态码会把调试端点变成内网端口/服务指纹工具。</summary>
    private static string DescribeUpstreamFailure(int status) => status switch
    {
        401 or 403 => "鉴权失败，请检查 API Key 与账号权限",
        404 => "接口地址或模型不存在，请检查地址与模型 ID",
        429 => "上游限流，请稍后再试",
        >= 500 => "上游服务暂时不可用，请稍后再试",
        _ => "上游拒绝了本次请求，请检查模型 ID 与协议"
    };

    private static void SetHeaders(HttpRequestMessage request, AiProviderConnectionEntity connection)
    {
        AiConnectionStore.ApplyAuthHeaders(request, connection.Protocol, connection.ApiKey);
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
        // 先定位模板：协议缺省时按模板决定，避免“选了 Gemini 模板但没传协议”被默认成 OpenAI。
        var templateId = string.IsNullOrWhiteSpace(dto.TemplateId)
            ? saved?.TemplateId ?? "custom"
            : dto.TemplateId;
        var template = AiProviderPreset.GetDefaults().FirstOrDefault(p => p.Id == templateId);
        if (template == null) templateId = "custom";
        var protocol = (dto.Protocol ?? saved?.Protocol ?? template?.Protocol ?? "openai").Trim();
        if (protocol is not ("openai" or "gemini")) return (null, "请选择 OpenAI 兼容或 Gemini 原生协议");
        var url = AiConnectionStore.NormalizeUrl(dto.ApiBaseUrl ?? "");
        if (url.Length > 2048 || !AiConnectionStore.IsValidUrl(url)) return (null, "请填写有效的 HTTP(S) 接口根地址，不包含查询参数");
        var timeout = dto.TimeoutSeconds ?? saved?.TimeoutSeconds ?? 60;
        if (timeout is < 5 or > 120) return (null, "连接超时需要在 5–120 秒之间");

        // 打码值只能在原 origin 下解析：否则“界面上没改的密钥”会被发往请求里填写的新地址。
        var originChanged = saved != null && !AiConnectionStore.SameOrigin(saved.ApiBaseUrl, url);
        var submittedMasked = dto.ApiKey is null || AiConnectionStore.IsMaskedSecret(saved?.ApiKey, dto.ApiKey);
        if (saved == null && AiConnectionStore.IsMaskedSecret(null, dto.ApiKey))
            return (null, "API Key 不能是打码值，请重新填写");
        if (originChanged && submittedMasked)
            return (null, "更换接口地址后请重新填写 API Key 与自定义请求头");
        var key = submittedMasked ? saved?.ApiKey ?? "" : dto.ApiKey!.Trim();
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
            var masked = headerValue == AiConnectionStore.SecretMask;
            if (masked && saved == null) return (null, "自定义请求头不能是打码值，请重新填写");
            // 新增的请求头没有旧值可解析：打码值只可能是从别处粘来的占位，不能悄悄存成空串。
            if (masked && !oldHeaders.ContainsKey(headerName))
                return (null, "新增的请求头不能是打码值，请填写真实值");
            if (masked && originChanged)
                return (null, "更换接口地址后请重新填写 API Key 与自定义请求头");
            var value = masked ? oldHeaders.GetValueOrDefault(headerName, "") : headerValue ?? "";
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
        // 省略 templateId 时保留原值，避免部分更新把模板重置成 custom（进而改变请求参数约束）。
        // 内置模板的协议就是请求体形状的契约：不允许把模板或协议改成互相冲突的组合。
        // 但旧版本可能已经留下错配行；只要本次没有改动模板/协议，仍然允许改名、密钥、模型等编辑，
        // 用户也可以把协议改成模板要求的值来修复它。
        var templateChanged = !string.IsNullOrWhiteSpace(dto.TemplateId)
            && !string.Equals(dto.TemplateId, saved?.TemplateId, StringComparison.Ordinal);
        var protocolChanged = dto.Protocol != null
            && !string.Equals(dto.Protocol.Trim(), saved?.Protocol, StringComparison.Ordinal);
        if (template is { Id: not "custom" }
            && !string.Equals(template.Protocol, protocol, StringComparison.Ordinal)
            && (saved == null || templateChanged || protocolChanged))
        {
            return (null, $"模板「{template.Name}」使用{(template.Protocol == "gemini" ? "Gemini 原生" : "OpenAI 兼容")}协议，与当前选择的协议不一致");
        }
        return (new AiProviderConnectionEntity
        {
            Id = saved?.Id ?? Guid.NewGuid().ToString("N"), Name = name,
            TemplateId = templateId,
            Protocol = protocol, ApiBaseUrl = url, ApiKey = key, TimeoutSeconds = timeout,
            HeadersJson = JsonSerializer.Serialize(headers, AiConnectionStore.JsonOptions),
            DefaultModel = defaultModel, ModelsJson = JsonSerializer.Serialize(models, AiConnectionStore.JsonOptions),
            CreatedAt = saved?.CreatedAt ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }, null);
    }
}
