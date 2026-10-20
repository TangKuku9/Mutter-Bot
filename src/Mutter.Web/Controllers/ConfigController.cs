using System.Text;
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
    private readonly Mutter.Core.Services.AiSearchCapabilityStore _capabilities;

    public ConfigController(
        AppDbContext dbContext,
        Mutter.Core.Services.AiSearchCapabilityStore capabilities)
    {
        _dbContext = dbContext;
        _capabilities = capabilities;
    }

    [HttpGet("ai")]
    public async Task<ActionResult<AiConfigDto>> GetAiConfig()
    {
        return Ok(await BuildAiConfigDtoAsync());
    }

    private async Task<AiConfigDto> BuildAiConfigDtoAsync()
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
            return new AiConfigDto
            {
                Temperature = 0.7,
                MaxTokens = 2048,
                MaxContextMessages = 20,
                MaxAttachedImages = AiConfig.DefaultMaxAttachedImages,
                WebSearchImageMode = AiConfig.WebSearchImageModeAuto,
                AvailableCharacters = characters,
                AvailableProviders = providers
            };
        }

        return new AiConfigDto
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
            WebSearchImageMode = string.IsNullOrWhiteSpace(entity.WebSearchImageMode)
                ? AiConfig.WebSearchImageModeAuto
                : entity.WebSearchImageMode,
            WebSearchImageCapable = entity.WebSearchImageCapable,
            WebSearchImageVerifiedAt = entity.WebSearchImageVerifiedAt,
            AvailableCharacters = characters,
            AvailableProviders = providers
        };
    }

    [HttpPut("ai")]
    public async Task<IActionResult> UpdateAiConfig([FromBody] AiConfigInputDto dto)
    {
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new AiConfigEntity();
            _dbContext.AiConfigs.Add(entity);
        }

        var error = NormalizeAndValidateAiConfigInput(dto);
        if (error != null)
            return BadRequest(new { error });

        ApplyNormalizedAiConfigInput(entity, dto, ResolveSubmittedApiKey(entity.ApiKey, dto.ApiKey));
        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

    // ===== AI 配置预设 =====

    private const int MaxAiPresets = 50;
    private const int MaxAiPresetConfigBytes = 512 * 1024;
    private static readonly JsonSerializerOptions PresetJsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("ai/presets")]
    public async Task<ActionResult<List<AiPresetSummaryDto>>> GetAiPresets()
    {
        var entities = await _dbContext.AiPresets
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync();
        var characters = BuildCharacterLookup(await BuildCharacterListAsync());
        return Ok(entities.Select(e => BuildAiPresetSummary(e, characters)).ToList());
    }

    [HttpGet("ai/presets/{id:int}")]
    public async Task<ActionResult<AiPresetDetailDto>> GetAiPreset(int id)
    {
        var entity = await _dbContext.AiPresets.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null) return NotFound(new { error = "预设不存在" });

        var characters = BuildCharacterLookup(await BuildCharacterListAsync());
        return Ok(BuildAiPresetDetail(entity, characters));
    }

    [HttpPost("ai/presets")]
    public async Task<ActionResult<AiPresetDetailDto>> CreateAiPreset([FromBody] AiPresetSaveDto dto)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        var nameError = await ValidatePresetNameAsync(name, null);
        if (nameError != null) return BadRequest(new { error = nameError });

        var description = (dto.Description ?? string.Empty).Trim();
        var descriptionError = ValidatePresetDescription(description);
        if (descriptionError != null) return BadRequest(new { error = descriptionError });

        if (await _dbContext.AiPresets.CountAsync() >= MaxAiPresets)
            return BadRequest(new { error = $"最多保存 {MaxAiPresets} 个配置预设，请先删除不用的预设" });

        var current = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        AiPresetConfigDto config;
        if (dto.Config == null)
        {
            config = SnapshotAiConfig(current);
        }
        else
        {
            var inputError = NormalizeAndValidateAiConfigInput(dto.Config);
            if (inputError != null) return BadRequest(new { error = inputError });
            config = ToPresetConfig(dto.Config);
        }

        var configJson = SerializePresetConfig(config);
        if (Encoding.UTF8.GetByteCount(configJson) > MaxAiPresetConfigBytes)
            return BadRequest(new { error = "预设内容过大，请缩短系统提示词后重试" });

        var entity = new AiConfigPresetEntity
        {
            Name = name,
            Description = description,
            ConfigJson = configJson,
            // 表单里的 Key 为空或仍是打码值时，取当前已保存的真 Key；取消勾选则明确不存。
            ApiKey = dto.IncludeApiKey
                ? ResolveSubmittedApiKey(current?.ApiKey, dto.Config?.ApiKey)
                : string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _dbContext.AiPresets.Add(entity);
        await _dbContext.SaveChangesAsync();

        var characters = BuildCharacterLookup(await BuildCharacterListAsync());
        return CreatedAtAction(nameof(GetAiPreset), new { id = entity.Id },
            BuildAiPresetDetail(entity, characters));
    }

    [HttpPut("ai/presets/{id:int}")]
    public async Task<ActionResult<AiPresetDetailDto>> UpdateAiPreset(int id, [FromBody] AiPresetUpdateDto dto)
    {
        var entity = await _dbContext.AiPresets.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null) return NotFound(new { error = "预设不存在" });

        var name = (dto.Name ?? string.Empty).Trim();
        var nameError = await ValidatePresetNameAsync(name, id);
        if (nameError != null) return BadRequest(new { error = nameError });

        var description = (dto.Description ?? string.Empty).Trim();
        var descriptionError = ValidatePresetDescription(description);
        if (descriptionError != null) return BadRequest(new { error = descriptionError });

        // 不传 Config = 只改名称/备注，快照和 Key 都保持原样。
        if (dto.Config != null)
        {
            var inputError = NormalizeAndValidateAiConfigInput(dto.Config);
            if (inputError != null) return BadRequest(new { error = inputError });

            var current = await _dbContext.AiConfigs.FirstOrDefaultAsync();
            entity.ApiKey = dto.IncludeApiKey switch
            {
                false => string.Empty,
                true => ResolveSubmittedApiKey(current?.ApiKey, dto.Config.ApiKey),
                null => entity.ApiKey
            };

            var configJson = SerializePresetConfig(ToPresetConfig(dto.Config));
            if (Encoding.UTF8.GetByteCount(configJson) > MaxAiPresetConfigBytes)
                return BadRequest(new { error = "预设内容过大，请缩短系统提示词后重试" });
            entity.ConfigJson = configJson;
        }

        entity.Name = name;
        entity.Description = description;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var characters = BuildCharacterLookup(await BuildCharacterListAsync());
        return Ok(BuildAiPresetDetail(entity, characters));
    }

    [HttpPost("ai/presets/{id:int}/duplicate")]
    public async Task<ActionResult<AiPresetDetailDto>> DuplicateAiPreset(int id)
    {
        var source = await _dbContext.AiPresets.FirstOrDefaultAsync(p => p.Id == id);
        if (source == null) return NotFound(new { error = "预设不存在" });

        if (await _dbContext.AiPresets.CountAsync() >= MaxAiPresets)
            return BadRequest(new { error = $"最多保存 {MaxAiPresets} 个配置预设，请先删除不用的预设" });

        var existingNames = await _dbContext.AiPresets.Select(p => p.Name).ToListAsync();
        var entity = new AiConfigPresetEntity
        {
            Name = BuildCopyName(source.Name, existingNames),
            Description = source.Description,
            ConfigJson = source.ConfigJson,
            ApiKey = source.ApiKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _dbContext.AiPresets.Add(entity);
        await _dbContext.SaveChangesAsync();

        var characters = BuildCharacterLookup(await BuildCharacterListAsync());
        return CreatedAtAction(nameof(GetAiPreset), new { id = entity.Id },
            BuildAiPresetDetail(entity, characters));
    }

    [HttpPost("ai/presets/{id:int}/apply")]
    public async Task<ActionResult<AiConfigDto>> ApplyAiPreset(int id)
    {
        var preset = await _dbContext.AiPresets.FirstOrDefaultAsync(p => p.Id == id);
        if (preset == null) return NotFound(new { error = "预设不存在" });

        if (!TryReadPresetConfig(preset.ConfigJson, out var config))
            return BadRequest(new { error = "预设数据损坏，无法应用；请删除后重新保存" });

        var error = NormalizeAndValidateAiConfigInput(config);
        if (error != null)
            return BadRequest(new { error = $"预设内容无效：{error}" });

        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        if (entity == null)
        {
            entity = new AiConfigEntity();
            _dbContext.AiConfigs.Add(entity);
        }

        // 预设没存 Key 就沿用当前 Key；存了则整份恢复，换服务商时不用重填。
        var apiKey = string.IsNullOrEmpty(preset.ApiKey) ? entity.ApiKey : preset.ApiKey;
        ApplyNormalizedAiConfigInput(entity, config, apiKey);
        await _dbContext.SaveChangesAsync();

        return Ok(await BuildAiConfigDtoAsync());
    }

    [HttpDelete("ai/presets/{id:int}")]
    public async Task<IActionResult> DeleteAiPreset(int id)
    {
        var entity = await _dbContext.AiPresets.FirstOrDefaultAsync(p => p.Id == id);
        if (entity == null) return NotFound(new { error = "预设不存在" });

        _dbContext.AiPresets.Remove(entity);
        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// 校验并就地裁剪可写配置。PUT /ai 与预设的创建/覆盖/应用共用，
    /// 防止几条写入路径对同一个字段给出不同结论。
    /// </summary>
    private static string? NormalizeAndValidateAiConfigInput(AiConfigInputDto dto)
    {
        dto.ApiBaseUrl = (dto.ApiBaseUrl ?? string.Empty).Trim();
        dto.ApiKey = (dto.ApiKey ?? string.Empty).Trim();
        dto.ModelName = (dto.ModelName ?? string.Empty).Trim();
        dto.SystemPrompt ??= string.Empty;
        dto.Personality ??= string.Empty;
        dto.Temperature = Math.Clamp(dto.Temperature, 0, 2);

        // 面板输入框有 min/max，但 API 可以绕过面板直连提交。
        // MaxTokens=0 会让输入预算算成 0、上下文被压到一条；MaxContextMessages=0 同理。
        dto.MaxTokens = Math.Clamp(dto.MaxTokens, 64, 32768);
        dto.MaxContextMessages = Math.Clamp(dto.MaxContextMessages, 2, 100);
        // 附图张数：负数是无效输入，过大只会白烧 token 与带宽
        dto.MaxAttachedImages = Math.Clamp(dto.MaxAttachedImages, 0, AiConfig.CeilingAttachedImages);

        var imageMode = (dto.WebSearchImageMode ?? string.Empty).Trim();
        if (imageMode.Length == 0) imageMode = AiConfig.WebSearchImageModeAuto;
        if (!AiConfig.IsValidWebSearchImageMode(imageMode))
            return "带图联网策略只能是 auto / two_stage / off";
        dto.WebSearchImageMode = imageMode;

        // 已选用的搜索策略：只接受探测矩阵里真实存在的形状，防止面板/脚本写入任意参数。
        var endpoint = (dto.WebSearchEndpoint ?? string.Empty).Trim();
        var shape = (dto.WebSearchShape ?? string.Empty).Trim();
        if (endpoint is not ("" or "openai" or "gemini"))
            return "联网搜索通道只能是 openai 或 gemini";
        if (shape.Length > 0 &&
            !Mutter.Core.Services.WebSearchProbe.Cases.Any(c => c.Shape == shape))
            return $"未知的联网搜索形状：{shape}";
        if ((shape == "gemini_native") != (endpoint == "gemini"))
            return "搜索形状与通道不匹配";

        dto.WebSearchEndpoint = endpoint;
        dto.WebSearchShape = shape;
        return null;
    }

    /// <summary>
    /// 把已校验的配置写进实体。调用方负责先解析出要落库的 Key。
    /// </summary>
    private void ApplyNormalizedAiConfigInput(AiConfigEntity entity, AiConfigInputDto dto, string apiKey)
    {
        // 先记住旧目标：下面会立刻把 dto 值写进 entity，再比较就永远相等了。
        var previousBaseUrl = entity.ApiBaseUrl;
        var previousModelName = entity.ModelName;

        entity.ApiBaseUrl = dto.ApiBaseUrl;
        entity.ApiKey = apiKey;
        entity.ModelName = dto.ModelName;
        entity.SystemPrompt = dto.SystemPrompt;
        entity.Personality = dto.Personality;
        entity.Temperature = dto.Temperature;
        entity.MaxTokens = dto.MaxTokens;
        entity.MaxContextMessages = dto.MaxContextMessages;
        entity.MaxAttachedImages = dto.MaxAttachedImages;
        entity.WebSearchEnabled = dto.WebSearchEnabled;
        entity.WebSearchImageMode = dto.WebSearchImageMode;

        var endpoint = dto.WebSearchEndpoint;
        var shape = dto.WebSearchShape;

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
        var targetBaseUrl = dto.ApiBaseUrl;
        var targetModel = dto.ModelName;

        // 先取本次探测对“新策略”的结论，再清运行时负缓存——Clear 会连探测账本一起清掉，
        // 顺序反了就会出现“刚测出带图可用、一保存又变成未测”。
        var probed = shape.Length > 0
            ? _capabilities.GetProbeImageCapability(targetBaseUrl, targetModel, shape)
            : null;
        if (targetChanged || strategyChanged)
            _capabilities.Clear(targetBaseUrl, targetModel, shape);

        entity.WebSearchEndpoint = endpoint;
        entity.WebSearchShape = shape;
        entity.WebSearchVerifiedAt = shape.Length == 0
            ? null
            : strategyChanged ? DateTime.UtcNow : entity.WebSearchVerifiedAt;

        // 图文能力是服务端探测结论，不从客户端接收。策略/目标变了就清空，等下一次探测或运行时验证。
        if (targetChanged || strategyChanged || shape.Length == 0)
        {
            entity.WebSearchImageCapable = null;
            entity.WebSearchImageVerifiedAt = null;
        }

        if (probed is bool imageCapable)
        {
            entity.WebSearchImageCapable = imageCapable;
            entity.WebSearchImageVerifiedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 表单里的 Key 为空或仍是打码值（说明用户没改），保持数据库中的原 Key。
    /// </summary>
    private static string ResolveSubmittedApiKey(string? currentKey, string? submittedKey)
    {
        currentKey ??= string.Empty;
        if (string.IsNullOrWhiteSpace(submittedKey) || submittedKey == MaskApiKey(currentKey))
            return currentKey;
        return submittedKey;
    }

    private static AiPresetConfigDto SnapshotAiConfig(AiConfigEntity? entity)
    {
        if (entity == null) return new AiPresetConfigDto();

        return new AiPresetConfigDto
        {
            Version = 1,
            ApiBaseUrl = entity.ApiBaseUrl,
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
            WebSearchImageMode = string.IsNullOrWhiteSpace(entity.WebSearchImageMode)
                ? AiConfig.WebSearchImageModeAuto
                : entity.WebSearchImageMode
        };
    }

    private static AiPresetConfigDto ToPresetConfig(AiConfigInputDto input) => new()
    {
        Version = 1,
        ApiBaseUrl = input.ApiBaseUrl,
        ModelName = input.ModelName,
        SystemPrompt = input.SystemPrompt,
        Personality = input.Personality,
        Temperature = input.Temperature,
        MaxTokens = input.MaxTokens,
        MaxContextMessages = input.MaxContextMessages,
        MaxAttachedImages = input.MaxAttachedImages,
        WebSearchEnabled = input.WebSearchEnabled,
        WebSearchEndpoint = input.WebSearchEndpoint,
        WebSearchShape = input.WebSearchShape,
        WebSearchImageMode = input.WebSearchImageMode
    };

    private static string SerializePresetConfig(AiPresetConfigDto config)
    {
        // 双保险：Key 只走单独一列，任何调用方都不可能把它序列化进快照。
        config.Version = 1;
        config.ApiKey = string.Empty;
        return JsonSerializer.Serialize(config, PresetJsonOptions);
    }

    private static bool TryReadPresetConfig(string? json, out AiPresetConfigDto config)
    {
        config = new AiPresetConfigDto();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<AiPresetConfigDto>(json, PresetJsonOptions);
            if (parsed == null) return false;
            config = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Dictionary<string, CharacterDto> BuildCharacterLookup(IEnumerable<CharacterDto> characters)
    {
        var lookup = new Dictionary<string, CharacterDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var character in characters)
            lookup[character.Name] = character;
        return lookup;
    }

    private static AiPresetSummaryDto BuildAiPresetSummary(
        AiConfigPresetEntity entity,
        IReadOnlyDictionary<string, CharacterDto> characters)
    {
        var valid = TryReadPresetConfig(entity.ConfigJson, out var config);
        var personality = config.Personality ?? string.Empty;
        var personalityMissing = false;
        var personalityDisplayName = string.Empty;

        if (!string.IsNullOrWhiteSpace(personality))
        {
            if (characters.TryGetValue(personality, out var character))
            {
                personalityDisplayName = character.DisplayName;
            }
            else
            {
                personalityDisplayName = personality;
                personalityMissing = true;
            }
        }

        return new AiPresetSummaryDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            ApiBaseUrl = config.ApiBaseUrl ?? string.Empty,
            ModelName = config.ModelName ?? string.Empty,
            Personality = personality,
            PersonalityDisplayName = personalityDisplayName,
            PersonalityMissing = personalityMissing,
            WebSearchEnabled = config.WebSearchEnabled,
            WebSearchShape = config.WebSearchShape ?? string.Empty,
            HasApiKey = !string.IsNullOrEmpty(entity.ApiKey),
            IsValid = valid,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private static AiPresetDetailDto BuildAiPresetDetail(
        AiConfigPresetEntity entity,
        IReadOnlyDictionary<string, CharacterDto> characters)
    {
        var summary = BuildAiPresetSummary(entity, characters);
        TryReadPresetConfig(entity.ConfigJson, out var config);
        config.ApiKey = string.Empty;

        return new AiPresetDetailDto
        {
            Id = summary.Id,
            Name = summary.Name,
            Description = summary.Description,
            ApiBaseUrl = summary.ApiBaseUrl,
            ModelName = summary.ModelName,
            Personality = summary.Personality,
            PersonalityDisplayName = summary.PersonalityDisplayName,
            PersonalityMissing = summary.PersonalityMissing,
            WebSearchEnabled = summary.WebSearchEnabled,
            WebSearchShape = summary.WebSearchShape,
            HasApiKey = summary.HasApiKey,
            IsValid = summary.IsValid,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            Config = config,
            ApiKey = MaskApiKey(entity.ApiKey)
        };
    }

    private async Task<string?> ValidatePresetNameAsync(string name, int? excludeId)
    {
        if (name.Length is < 1 or > 40)
            return "预设名称需为 1-40 个字符";

        var names = await _dbContext.AiPresets
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();
        return names.Any(p =>
            (!excludeId.HasValue || p.Id != excludeId.Value) &&
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ? $"预设「{name}」已存在"
            : null;
    }

    private static string? ValidatePresetDescription(string description) =>
        description.Length > 200 ? "预设备注不能超过 200 个字符" : null;

    private static string BuildCopyName(string sourceName, IEnumerable<string> existingNames)
    {
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        const string suffix = " 副本";
        var baseName = TrimToLength(sourceName, 40 - suffix.Length) + suffix;
        if (!names.Contains(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var numbered = $"{suffix} {i}";
            var candidate = TrimToLength(sourceName, 40 - numbered.Length) + numbered;
            if (!names.Contains(candidate)) return candidate;
        }

        return Guid.NewGuid().ToString("N")[..8];
    }

    private static string TrimToLength(string value, int max) =>
        value.Length <= max ? value : value[..max];

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
    private async IAsyncEnumerable<WebSearchProbeEventDto> RunProbeEventsAsync(
        string apiBaseUrl, string apiKey, string model,
        Mutter.Core.Services.WebSearchProbeRunner runner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
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

        // 图文基线：同一端点、同一模型、带图但不带搜索。带图探测的 token/内容差分必须和它比，
        // 不能拿纯文本基线去比——图片本身就会拉高 prompt token。
        var (imageBaseStatus, imageBaseRoot, _) =
            await runner.SendImageAsync(apiBaseUrl, apiKey, "openai", null, model, probeCt);
        var imageBaseTokens = imageBaseStatus is null
            ? null
            : Mutter.Core.Services.WebSearchProbe.ReadPromptTokens(imageBaseRoot ?? default);
        var imageBaseText = imageBaseRoot is null
            ? null
            : Mutter.Core.Services.WebSearchProbe.ReadResponseText(imageBaseRoot.Value);

        // Gemini 原生通道要有自己的基线：同一模型、同一端点、不带工具。内容差分必须同端点比，
        // 否则兼容层与原生层的行为差异会被误当成检索证据。
        int? geminiBaseTokens = null;
        string? geminiBaseText = null;
        var geminiBaselineTried = false;

        int? geminiImageBaseTokens = null;
        string? geminiImageBaseText = null;
        var geminiImageBaselineTried = false;

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
                Evidence = verdict.Evidence,
                ImageStatus = "not_tested",
                ImageNote = "文字联网未通过，未测带图"
            };

            // 只有文字形状已验证才值得再花一次请求测图文组合；否则先证明搜索本身能用。
            if (verdict.Status == "verified")
            {
                if (isGemini && !geminiImageBaselineTried)
                {
                    geminiImageBaselineTried = true;
                    var (gis, gir, _) = await runner.SendImageAsync(
                        apiBaseUrl, apiKey, "gemini", null, model, probeCt);
                    geminiImageBaseTokens = gis is null
                        ? null
                        : Mutter.Core.Services.WebSearchProbe.ReadPromptTokens(gir ?? default);
                    geminiImageBaseText = gir is null
                        ? null
                        : Mutter.Core.Services.WebSearchProbe.ReadResponseText(gir.Value);
                }

                var (imageStatus, imageRoot, imageError) = await runner.SendImageAsync(
                    apiBaseUrl, apiKey, probeCase.Endpoint, probeCase.Shape, model, probeCt);
                var imageCmpTokens = isGemini
                    ? geminiImageBaseTokens ?? imageBaseTokens
                    : imageBaseTokens;
                var imageCmpText = isGemini
                    ? geminiImageBaseText ?? imageBaseText
                    : imageBaseText;
                int? imageDelta = imageStatus is not null && imageCmpTokens is int ib
                    ? Mutter.Core.Services.WebSearchProbe.ReadPromptTokens(imageRoot ?? default) - ib
                    : null;
                var imageText = imageRoot is null
                    ? null
                    : Mutter.Core.Services.WebSearchProbe.ReadResponseText(imageRoot.Value);
                var imageVerdict = imageStatus is null
                    ? new Mutter.Core.Services.WebSearchProbeVerdict("unreachable", Note: imageError)
                    : Mutter.Core.Services.WebSearchProbe.ImageVerdict(
                        probeCase.Shape, imageStatus, imageRoot, imageDelta, imageCmpText, imageText);
                if (probeCase.Endpoint == "gemini" && imageStatus == 404)
                    imageVerdict = new Mutter.Core.Services.WebSearchProbeVerdict(
                        "no_native_endpoint", Note: "该地址下没有找到 Gemini 原生 :generateContent 端点");

                result.ImageStatus = imageVerdict.Status;
                result.ImageNote = imageVerdict.Note;
                result.ImageEvidence = imageVerdict.Evidence;
                result.ImagePromptTokensDelta = imageVerdict.PromptTokensDelta;

                // 结论明确才记账：超时/5xx/无原生端点不代表“图文不支持”，不该污染下次选用。
                if (imageVerdict.Status is "verified" or "image_rejected" or "no_evidence" or "rejected")
                {
                    _capabilities.RecordProbe(
                        apiBaseUrl, model, probeCase.Shape,
                        imageCapable: imageVerdict.Status == "verified");
                }
            }

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
        if (aiConfig != null &&
            string.Equals(aiConfig.Personality, name, StringComparison.OrdinalIgnoreCase))
            aiConfig.Personality = string.Empty;

        var groupConfigs = await _dbContext.GroupConfigs
            .Where(g => g.ActiveCharacter == name).ToListAsync();
        foreach (var g in groupConfigs)
            g.ActiveCharacter = null;

        // 预设里引用同一人设的也清掉，否则应用预设时那个标识会被当成自由文本性格塞进提示词。
        var presets = await _dbContext.AiPresets.ToListAsync();
        foreach (var preset in presets)
        {
            if (!TryReadPresetConfig(preset.ConfigJson, out var config)) continue;
            if (!string.Equals(config.Personality, name, StringComparison.OrdinalIgnoreCase)) continue;

            config.Personality = string.Empty;
            preset.ConfigJson = SerializePresetConfig(config);
            preset.UpdatedAt = DateTime.UtcNow;
        }

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

    /// <summary>图文组合的判定：verified / image_rejected / no_evidence / not_tested 等。</summary>
    public string ImageStatus { get; set; } = "not_tested";
    public string? ImageNote { get; set; }
    public string? ImageEvidence { get; set; }
    public int? ImagePromptTokensDelta { get; set; }
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
