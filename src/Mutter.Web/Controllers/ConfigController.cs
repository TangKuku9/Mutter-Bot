using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Web.Models;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ConfigController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("ai")]
    public async Task<ActionResult<AiConfigDto>> GetAiConfig()
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        var characters = await BuildCharacterListAsync();

        var providers = AiProviderPreset.GetDefaults().Select(p => new ProviderPresetDto
        {
            Id = p.Id,
            Name = p.Name,
            ApiBaseUrl = p.ApiBaseUrl,
            DefaultModel = p.DefaultModel,
            Models = p.Models,
            WebSearchSupported = p.WebSearchSupported,
            WebSearchMode = p.WebSearchMode,
            Description = p.Description,
            KeyHelpUrl = p.KeyHelpUrl,
            KeyHelpText = p.KeyHelpText
        }).ToList();

        if (entity == null)
        {
            return Ok(new AiConfigDto
            {
                Temperature = 0.7,
                MaxTokens = 2048,
                MaxContextMessages = 20,
                MaxAttachedImages = AiConfig.DefaultMaxAttachedImages,
                AvailableCharacters = characters,
                AvailableProviders = providers
            });
        }

        return Ok(new AiConfigDto
        {
            ApiBaseUrl = entity.ApiBaseUrl,
            ApiKey = MaskApiKey(entity.ApiKey),
            ModelName = entity.ModelName,
            SystemPrompt = entity.SystemPrompt,
            Personality = entity.Personality,
            Temperature = entity.Temperature,
            MaxTokens = entity.MaxTokens,
            MaxContextMessages = entity.MaxContextMessages,
            MaxAttachedImages = entity.MaxAttachedImages,
            WebSearchEnabled = entity.WebSearchEnabled,
            WebSearchEndpoint = entity.WebSearchEndpoint,
            WebSearchShape = entity.WebSearchShape,
            WebSearchVerifiedAt = entity.WebSearchVerifiedAt,
            AvailableCharacters = characters,
            AvailableProviders = providers
        });
    }

    [HttpPut("ai")]
    public async Task<IActionResult> UpdateAiConfig([FromBody] AiConfigDto dto)
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new AiConfigEntity();
            _dbContext.AiConfigs.Add(entity);
        }

        // 先记住旧目标：下面会立刻把 dto 值写进 entity，再比较就永远相等了。
        var previousBaseUrl = entity.ApiBaseUrl;
        var previousModelName = entity.ModelName;

        entity.ApiBaseUrl = dto.ApiBaseUrl;
        // ApiKey 为空或仍是打码值时视为未修改，保留数据库中的原 Key
        if (!string.IsNullOrWhiteSpace(dto.ApiKey) && dto.ApiKey != MaskApiKey(entity.ApiKey))
        {
            entity.ApiKey = dto.ApiKey;
        }
        entity.ModelName = dto.ModelName;
        entity.SystemPrompt = dto.SystemPrompt;
        entity.Personality = dto.Personality;
        entity.Temperature = dto.Temperature;
        // 这三个也钳制：面板的输入框有 min/max，但 API 可以绕过面板直连提交。
        // MaxTokens=0 会让输入预算算成 0、上下文被压到一条；MaxContextMessages=0 同理。
        entity.MaxTokens = Math.Clamp(dto.MaxTokens, 64, 32768);
        entity.MaxContextMessages = Math.Clamp(dto.MaxContextMessages, 2, 100);
        // 附图张数在这里钳制：负数是无效输入，过大只会白烧 token 与带宽
        entity.MaxAttachedImages = Math.Clamp(dto.MaxAttachedImages, 0, AiConfig.CeilingAttachedImages);
        entity.WebSearchEnabled = dto.WebSearchEnabled;

        // 已选用的搜索策略：只接受探测矩阵里真实存在的形状，防止面板/脚本写入任意参数。
        var endpoint = (dto.WebSearchEndpoint ?? string.Empty).Trim();
        var shape = (dto.WebSearchShape ?? string.Empty).Trim();
        if (endpoint is not ("" or "openai" or "gemini"))
            return BadRequest(new { error = "联网搜索通道只能是 openai 或 gemini" });
        if (shape.Length > 0 &&
            !Mutter.Core.Services.WebSearchProbe.Cases.Any(c => c.Shape == shape))
            return BadRequest(new { error = $"未知的联网搜索形状：{shape}" });
        if ((shape == "gemini_native") != (endpoint == "gemini"))
            return BadRequest(new { error = "搜索形状与通道不匹配" });

        // 换了地址或模型，旧策略的验证结论不再可信：除了本次同时选用的新策略，一律清空回退自动推断。
        var targetChanged = !string.Equals(previousBaseUrl, dto.ApiBaseUrl, StringComparison.Ordinal)
                         || !string.Equals(previousModelName, dto.ModelName, StringComparison.Ordinal);
        if (targetChanged &&
            string.Equals(shape, entity.WebSearchShape, StringComparison.Ordinal) &&
            string.Equals(endpoint, entity.WebSearchEndpoint, StringComparison.Ordinal))
        {
            shape = string.Empty;
            endpoint = string.Empty;
        }

        var strategyChanged = !string.Equals(shape, entity.WebSearchShape, StringComparison.Ordinal)
                           || !string.Equals(endpoint, entity.WebSearchEndpoint, StringComparison.Ordinal);
        entity.WebSearchEndpoint = endpoint;
        entity.WebSearchShape = shape;
        entity.WebSearchVerifiedAt = shape.Length == 0
            ? null
            : strategyChanged ? DateTime.UtcNow : entity.WebSearchVerifiedAt;

        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// 联网搜索探测（一次性返回）：保留给脚本/旧前端；面板用流式版本看逐项进度。
    /// </summary>
    [HttpPost("ai/probe-websearch")]
    public async Task<IActionResult> ProbeWebSearch(
        [FromBody] WebSearchProbeDto dto,
        [FromServices] Mutter.Core.Services.WebSearchProbeRunner runner,
        CancellationToken ct)
    {
        if (!TryReadProbeTarget(dto, out var apiBaseUrl, out var model, out var badRequest))
            return BadRequest(new { error = badRequest });

        var apiKey = await ResolveProbeApiKeyAsync(dto);
        if (apiKey is null)
            return BadRequest(new { error = "尚未保存过 API Key，请先填写并保存配置再探测" });

        WebSearchProbeReportDto? report = null;
        WebSearchProbeEventDto? failure = null;
        await foreach (var ev in RunProbeEventsAsync(apiBaseUrl, apiKey, model, runner, ct))
        {
            if (ev.Type == "done") report = ev.Report;
            else if (ev.Type == "error") failure = ev;
        }

        if (failure is not null)
            return Ok(new WebSearchProbeReportDto
            {
                Reachable = failure.Reachable ?? false,
                AuthFailed = failure.AuthFailed ?? false,
                Error = failure.Error,
                Results = []
            });

        return Ok(report);
    }

    /// <summary>
    /// 联网搜索探测（NDJSON 流）：每完成一个形状推一行，面板据此显示转圈/打钩。
    /// 事件顺序：start（用例清单）→ 若干 case（单个结果）→ done（完整报告）；失败推 error。
    /// </summary>
    [HttpPost("ai/probe-websearch/stream")]
    public async Task ProbeWebSearchStream(
        [FromBody] WebSearchProbeDto dto,
        [FromServices] Mutter.Core.Services.WebSearchProbeRunner runner,
        CancellationToken ct)
    {
        if (!TryReadProbeTarget(dto, out var apiBaseUrl, out var model, out var badRequest))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = badRequest }, ct);
            return;
        }

        var apiKey = await ResolveProbeApiKeyAsync(dto);
        if (apiKey is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = "尚未保存过 API Key，请先填写并保存配置再探测" }, ct);
            return;
        }

        // 逐行 JSON，前端用 fetch + ReadableStream 解析；关闭中间层缓冲，保证逐项到达
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            await foreach (var ev in RunProbeEventsAsync(apiBaseUrl, apiKey, model, runner, ct))
            {
                await Response.WriteAsync(JsonSerializer.Serialize(ev, jsonOptions) + "\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 面板关闭弹窗/断开连接：停止探测即可
        }
    }

    /// <summary>探测目标的最小校验。</summary>
    private static bool TryReadProbeTarget(
        WebSearchProbeDto dto, out string apiBaseUrl, out string model, out string error)
    {
        apiBaseUrl = dto.ApiBaseUrl?.Trim() ?? string.Empty;
        model = dto.ModelName?.Trim() ?? string.Empty;
        error = string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(model)
            ? "探测前先填写 API 地址与模型名"
            : string.Empty;
        return error.Length == 0;
    }

    /// <summary>Key 传打码值说明表单没改：探测必须用真 Key，从库里取。</summary>
    private async Task<string?> ResolveProbeApiKeyAsync(WebSearchProbeDto dto)
    {
        var apiKey = dto.ApiKey;
        var saved = (await _dbContext.AiConfigs.FirstOrDefaultAsync())?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == MaskApiKey(saved ?? string.Empty))
            return string.IsNullOrWhiteSpace(saved) ? null : saved;
        return apiKey;
    }

    /// <summary>
    /// 探测的单一执行路径：一次性接口与流式接口都消费这里，避免两套逻辑漂移。
    /// </summary>
    private static async IAsyncEnumerable<WebSearchProbeEventDto> RunProbeEventsAsync(
        string apiBaseUrl, string apiKey, string model,
        Mutter.Core.Services.WebSearchProbeRunner runner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, overallCts.Token);
        var probeCt = probeCts.Token;

        yield return new WebSearchProbeEventDto
        {
            Type = "start",
            Cases = Mutter.Core.Services.WebSearchProbe.Cases
                .Select(c => new WebSearchProbeCaseDto { Shape = c.Shape, Description = c.Description })
                .ToList()
        };

        // 基线：OpenAI 兼容端不带任何搜索形状，供 token 差分与内容差分。基线失败则整个探测没有意义。
        var (baseStatus, baseRoot, baseError) = await runner.SendAsync(apiBaseUrl, apiKey, "openai", null, model, probeCt);
        if (baseStatus is null)
        {
            yield return new WebSearchProbeEventDto { Type = "error", Reachable = false, Error = $"上游不可达：{baseError}" };
            yield break;
        }
        if (baseStatus is 401 or 403)
        {
            yield return new WebSearchProbeEventDto
            {
                Type = "error", Reachable = true, AuthFailed = true,
                Error = $"鉴权失败（{baseStatus}）：Key 无效或无权限，搜索结论无从谈起"
            };
            yield break;
        }

        var baseTokens = Mutter.Core.Services.WebSearchProbe.ReadPromptTokens(baseRoot ?? default);
        var baseText = Mutter.Core.Services.WebSearchProbe.ReadResponseText(baseRoot ?? default);

        // Gemini 原生通道要有自己的基线：同一模型、同一端点、不带工具。内容差分必须同端点比，
        // 否则兼容层与原生层的行为差异会被误当成检索证据。
        int? geminiBaseTokens = null;
        string? geminiBaseText = null;
        var geminiBaselineTried = false;

        var results = new List<WebSearchProbeResultDto>();
        foreach (var probeCase in Mutter.Core.Services.WebSearchProbe.Cases)
        {
            var isGemini = probeCase.Endpoint == "gemini";
            if (isGemini && !geminiBaselineTried)
            {
                geminiBaselineTried = true;
                var (gs, gr, _) = await runner.SendAsync(apiBaseUrl, apiKey, "gemini", null, model, probeCt);
                geminiBaseTokens = gs is null ? null : Mutter.Core.Services.WebSearchProbe.ReadPromptTokens(gr ?? default);
                geminiBaseText = gr is null ? null : Mutter.Core.Services.WebSearchProbe.ReadResponseText(gr.Value);
            }

            var (status, root, error) = await runner.SendAsync(apiBaseUrl, apiKey, probeCase.Endpoint, probeCase.Shape, model, probeCt);
            var cmpTokens = isGemini ? geminiBaseTokens ?? baseTokens : baseTokens;
            var cmpText = isGemini ? geminiBaseText ?? baseText : baseText;
            int? delta = status is not null && cmpTokens is int b
                ? Mutter.Core.Services.WebSearchProbe.ReadPromptTokens(root ?? default) - b
                : null;
            var responseText = root is null ? null : Mutter.Core.Services.WebSearchProbe.ReadResponseText(root.Value);
            var verdict = status is null
                ? new Mutter.Core.Services.WebSearchProbeVerdict("unreachable", Note: error)
                : Mutter.Core.Services.WebSearchProbe.Verdict(probeCase.Shape, status, root, delta, cmpText, responseText);
            // 原生端点整条路径都 404 时不是"形状被拒"，是这地址没挂原生端点——换说人话
            if (probeCase.Endpoint == "gemini" && status == 404)
                verdict = new Mutter.Core.Services.WebSearchProbeVerdict("no_native_endpoint",
                    Note: "该地址下没有找到 Gemini 原生 :generateContent 端点，无法探测原生通道");

            var result = new WebSearchProbeResultDto
            {
                Shape = probeCase.Shape,
                Description = probeCase.Description,
                Status = verdict.Status,
                GroundingField = verdict.GroundingField,
                Note = verdict.Note,
                PromptTokensDelta = verdict.PromptTokensDelta,
                Evidence = verdict.Evidence
            };
            results.Add(result);
            yield return new WebSearchProbeEventDto { Type = "case", Result = result };
        }

        yield return new WebSearchProbeEventDto
        {
            Type = "done",
            Report = new WebSearchProbeReportDto
            {
                Reachable = true,
                AuthFailed = false,
                BasePromptTokens = baseTokens,
                Results = results
            }
        };
    }

    /// <summary>
    /// 全量人设列表（内置 + 自定义）。
    /// </summary>
    [HttpGet("characters")]
    public async Task<ActionResult<List<CharacterDto>>> GetCharacters()
    {
        return Ok(await BuildCharacterListAsync());
    }

    /// <summary>
    /// 新增自定义人设。
    /// </summary>
    [HttpPost("characters")]
    public async Task<IActionResult> CreateCharacter([FromBody] CharacterDto dto)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        if (!IsValidCharacterName(name))
            return BadRequest(new { error = "人设标识需为 1-32 位字母/数字/下划线/短横线" });
        if (AiCharacter.IsReservedName(name))
            return BadRequest(new { error = $"标识 {name} 与内置人设冲突，请换一个" });
        if (await _dbContext.CustomCharacters.AnyAsync(c => c.Name == name))
            return BadRequest(new { error = $"人设 {name} 已存在" });
        if (string.IsNullOrWhiteSpace(dto.DisplayName))
            return BadRequest(new { error = "人设名称不能为空" });

        _dbContext.CustomCharacters.Add(new CustomCharacterEntity
        {
            Name = name,
            DisplayName = dto.DisplayName.Trim(),
            Description = dto.Description ?? string.Empty,
            SystemPrompt = dto.SystemPrompt ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// 更新自定义人设（标识不可修改）。
    /// </summary>
    [HttpPut("characters/{name}")]
    public async Task<IActionResult> UpdateCharacter(string name, [FromBody] CharacterDto dto)
    {
        var entity = await _dbContext.CustomCharacters.FirstOrDefaultAsync(c => c.Name == name);
        if (entity == null)
            return BadRequest(new { error = $"自定义人设 {name} 不存在" });
        if (string.IsNullOrWhiteSpace(dto.DisplayName))
            return BadRequest(new { error = "人设名称不能为空" });

        entity.DisplayName = dto.DisplayName.Trim();
        entity.Description = dto.Description ?? string.Empty;
        entity.SystemPrompt = dto.SystemPrompt ?? string.Empty;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// 删除自定义人设，并清理仍在引用它的配置。
    /// </summary>
    [HttpDelete("characters/{name}")]
    public async Task<IActionResult> DeleteCharacter(string name)
    {
        var entity = await _dbContext.CustomCharacters.FirstOrDefaultAsync(c => c.Name == name);
        if (entity == null)
            return BadRequest(new { error = $"自定义人设 {name} 不存在" });

        _dbContext.CustomCharacters.Remove(entity);

        // 清理引用：全局人设与各群人设若指向被删人设则回退为空（默认）
        var aiConfig = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        if (aiConfig != null && aiConfig.Personality == name)
            aiConfig.Personality = string.Empty;

        var groupConfigs = await _dbContext.GroupConfigs
            .Where(g => g.ActiveCharacter == name).ToListAsync();
        foreach (var g in groupConfigs)
            g.ActiveCharacter = null;

        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// 内置人设 + 自定义人设合并列表，内置在前。
    /// </summary>
    private async Task<List<CharacterDto>> BuildCharacterListAsync()
    {
        var list = AiCharacter.GetDefaults().Select(c => new CharacterDto
        {
            Name = c.Name,
            DisplayName = c.DisplayName,
            Description = c.Description,
            SystemPrompt = c.SystemPromptOverride,
            IsCustom = false
        }).ToList();

        var customs = await _dbContext.CustomCharacters
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CharacterDto
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                Description = c.Description,
                SystemPrompt = c.SystemPrompt,
                IsCustom = true
            }).ToListAsync();

        list.AddRange(customs);
        return list;
    }

    private static bool IsValidCharacterName(string name) =>
        name.Length is >= 1 and <= 32 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    [HttpGet("qqbot")]
    public ActionResult<QqBotStatusDto> GetQqBotConfig(
        [FromServices] IQqGatewayConnection connection,
        [FromServices] IOptions<MutterOptions> options)
    {
        var o = options.Value;
        return Ok(new QqBotStatusDto
        {
            AppId = MaskAppId(o.AppId),
            ApiBaseUrl = o.ApiBaseUrl,
            IsConnected = connection.IsConnected
        });
    }

    /// <summary>
    /// 将 ApiKey 打码，仅显示前4位和后4位，中间用 **** 替代。
    /// 长度不足8位时全部打码为 ****。
    /// </summary>
    private static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return string.Empty;

        // 短 Key（<=8字符）无法安全分段展示，统一返回 ****
        if (apiKey.Length <= 8)
            return "****";

        // 格式：前4位 + **** + 后4位，例如 "sk-a****xyz"
        return $"{apiKey[..4]}****{apiKey[^4..]}";
    }

    private static string MaskAppId(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
            return string.Empty;

        if (appId.Length <= 8)
            return "****";

        return $"{appId[..4]}****{appId[^4..]}";
    }
}

public class QqBotStatusDto
{
    public string AppId { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
}

public class WebSearchProbeDto
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
}

public class WebSearchProbeResultDto
{
    public string Shape { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? GroundingField { get; set; }
    public string? Note { get; set; }
    public int? PromptTokensDelta { get; set; }

    /// <summary>证据类型：grounding_metadata / prompt_token_delta / content_time。</summary>
    public string? Evidence { get; set; }
}

public class WebSearchProbeReportDto
{
    public bool Reachable { get; set; }
    public bool AuthFailed { get; set; }
    public string? Error { get; set; }
    public int? BasePromptTokens { get; set; }
    public List<WebSearchProbeResultDto> Results { get; set; } = [];
}

/// <summary>流式探测的一行事件。前端按 type 分发：start/case/done/error。</summary>
public class WebSearchProbeEventDto
{
    public string Type { get; set; } = string.Empty;
    public List<WebSearchProbeCaseDto>? Cases { get; set; }
    public WebSearchProbeResultDto? Result { get; set; }
    public WebSearchProbeReportDto? Report { get; set; }
    public string? Error { get; set; }
    public bool? Reachable { get; set; }
    public bool? AuthFailed { get; set; }
}

public class WebSearchProbeCaseDto
{
    public string Shape { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
