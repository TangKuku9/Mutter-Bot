using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mutter.Core.Configuration;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Services.WebSearch;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;
using Mutter.Infrastructure.Repositories;
using Mutter.Web.Models;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class ConfigController : ControllerBase
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
        var connections = await AiConnectionsController.BuildListAsync(_dbContext);
        var connection = entity == null ? null : await _dbContext.AiProviderConnections
            .AsNoTracking().FirstOrDefaultAsync(c => c.Id == entity.ProviderConnectionId);

        var providers = AiProviderPreset.GetDefaults().Select(p => new ProviderPresetDto
        {
            Category = p.Category,
            Protocol = p.Protocol,
            ModelsUpdatedAt = p.ModelsUpdatedAt,
            DocumentationUrl = p.DocumentationUrl,
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
                ExternalSearch = new ExternalSearchInputDto(),
                ExternalSearchStatus = new ExternalSearchStatusDto(),
                AvailableCharacters = characters,
                Connections = connections,
                AvailableProviders = providers
            };
        }

        var external = ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson);
        // Key 只回显“是否已保存”：部分打码会泄露首尾 8 个字符，未绑定连接的旧配置也要用完整掩码。
        var hasKey = connection != null ? connection.ApiKey.Length > 0 : entity.ApiKey.Length > 0;
        return new AiConfigDto
        {
            ProviderConnectionId = entity.ProviderConnectionId,
            ProviderConnectionVersion = connection?.Revision ?? "",
            Connections = connections,
            ApiBaseUrl = connection?.ApiBaseUrl ?? entity.ApiBaseUrl,
            ApiKey = hasKey ? AiConnectionStore.SecretMask : "",
            ModelName = entity.ModelName,
            SystemPrompt = entity.SystemPrompt,
            Personality = entity.Personality,
            Temperature = entity.Temperature,
            MaxTokens = entity.MaxTokens,
            MaxContextMessages = entity.MaxContextMessages,
            ContextWindowTokens = entity.ContextWindowTokens,
            MaxInputTokens = entity.MaxInputTokens,
            GroupMemoryInjectionEnabled = entity.GroupMemoryInjectionEnabled,
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
            ExternalSearch = new ExternalSearchInputDto
            {
                Enabled = external.Enabled ?? false,
                Provider = external.Provider,
                BaseUrl = external.BaseUrl,
                // SearXNG 不使用 Key：不回显旧服务商的残留 Key；其余服务（含自定义通道）只回显完整掩码。
                ApiKey = ExternalSearchConfig.SupportsApiKey(external.Provider) && external.ApiKey.Length > 0
                    ? AiConnectionStore.SecretMask
                    : string.Empty,
                MaxResults = external.MaxResults,
                TimeoutSeconds = external.TimeoutSeconds,
                FallbackToBuiltin = external.FallbackToBuiltin
            },
            ExternalSearchStatus = new ExternalSearchStatusDto
            {
                VerifiedAt = external.VerifiedAt
            },
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

        var error = await ResolveConnectionAsync(dto)
            ?? NormalizeAndValidateAiConfigInput(dto, ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson));
        if (error != null)
            return BadRequest(new { error });

        var wasBound = entity.ProviderConnectionId.Length > 0;
        // 未绑定的旧配置可能还留着密钥副本：换了地址 origin 又不重填 Key 时，
        // 打码值只代表“旧地址的旧 Key”，不能跟着新地址落库。地址留空不算换地址（部分更新）。
        if (dto.ProviderConnectionId.Length == 0 && !wasBound && entity.ApiKey.Length > 0
            && dto.ApiBaseUrl.Length > 0
            && (string.IsNullOrWhiteSpace(dto.ApiKey) || IsMaskedKey(entity.ApiKey, dto.ApiKey))
            && !AiConnectionStore.SameOrigin(entity.ApiBaseUrl, dto.ApiBaseUrl))
            return BadRequest(new { error = "更换接口地址后请重新填写 API Key" });
        var apiKey = dto.ProviderConnectionId.Length > 0
            ? dto.ApiKey
            // 解绑连接时不要把连接密钥的兼容副本继续留在主配置里；只有显式重填才写新值。
            : wasBound && (string.IsNullOrWhiteSpace(dto.ApiKey) || IsMaskedKey(entity.ApiKey, dto.ApiKey))
                ? string.Empty
                : ResolveSubmittedApiKey(entity.ApiKey, dto.ApiKey);
        ApplyNormalizedAiConfigInput(entity, dto, apiKey);
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
            // 服务端快照必须自带当前连接版本，否则下面的版本校验会把快照里的搜索策略当成过期清掉。
            if (config.ProviderConnectionId.Length > 0)
                config.ProviderConnectionVersion = await _dbContext.AiProviderConnections.AsNoTracking()
                    .Where(c => c.Id == config.ProviderConnectionId).Select(c => c.Revision)
                    .FirstOrDefaultAsync() ?? string.Empty;
            var connectionError = await ResolveConnectionAsync(config);
            if (connectionError != null) return BadRequest(new { error = connectionError });
        }
        else
        {
            var inputError = await ResolveConnectionAsync(dto.Config) ?? NormalizeAndValidateAiConfigInput(dto.Config);
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
            ApiKey = config.ProviderConnectionId.Length == 0 && dto.IncludeApiKey
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
            var inputError = await ResolveConnectionAsync(dto.Config) ?? NormalizeAndValidateAiConfigInput(dto.Config);
            if (inputError != null) return BadRequest(new { error = inputError });

            entity.ApiKey = dto.Config.ProviderConnectionId.Length > 0 ? string.Empty : dto.IncludeApiKey switch
            {
                false => string.Empty,
                // 打码值只能解析成“这个预设自己的”旧 Key；拿主配置的 Key 顶替会把另一个账号的密钥写进预设。
                true => ResolveSubmittedApiKey(entity.ApiKey, dto.Config.ApiKey),
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

        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync();

        if (config.ProviderConnectionId.Length == 0 && await _dbContext.AiProviderConnections.AnyAsync())
            return BadRequest(new { error = "该旧预设尚未绑定服务商连接：请在「管理预设」中编辑它并选择连接与模型后再应用" });
        var error = await ResolveConnectionAsync(config)
            ?? NormalizeAndValidateAiConfigInput(config, allowMissingExternalSearchKey: true);
        if (error != null)
            return BadRequest(new { error = $"预设内容无效：{error}" });

        if (entity == null)
        {
            entity = new AiConfigEntity();
            _dbContext.AiConfigs.Add(entity);
        }

        // 预设不保存搜索 Key，只能从完全相同的服务商和地址继承。
        // 恢复时显式写入这个结果，不能再走普通表单“空值=沿用”的解析路径。
        if (config.ExternalSearch is { } presetExternal)
        {
            var currentExternal = ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson);
            presetExternal.ApiKey = string.Equals(currentExternal.Provider, presetExternal.Provider, StringComparison.Ordinal)
                && string.Equals(currentExternal.BaseUrl.TrimEnd('/'), presetExternal.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
                    ? currentExternal.ApiKey : string.Empty;
        }

        // 预设没存 Key 就沿用当前 Key；存了则整份恢复，换服务商时不用重填。
        var apiKey = config.ProviderConnectionId.Length > 0 ? config.ApiKey
            : string.IsNullOrEmpty(preset.ApiKey) ? entity.ApiKey : preset.ApiKey;
        ApplyNormalizedAiConfigInput(entity, config, apiKey, applyingPreset: true);
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
    /// <paramref name="currentExternal"/> 仅 PUT /ai 传入（当前已保存的外搜配置）：
    /// 服务商变更时清掉「沿用旧服务商」的 Key/地址提交，让必填校验生效；
    /// <paramref name="allowMissingExternalSearchKey"/> 仅应用预设时传入：
    /// 预设不含外搜 Key，允许先应用配置（面板提示重填），避免目标服务商变化时整份预设无法应用。
    /// </summary>
    private static string? NormalizeAndValidateAiConfigInput(AiConfigInputDto dto,
        ExternalSearchConfig? currentExternal = null, bool allowMissingExternalSearchKey = false)
    {
        dto.ApiBaseUrl = (dto.ApiBaseUrl ?? string.Empty).Trim();
        dto.ApiKey = (dto.ApiKey ?? string.Empty).Trim();
        dto.ModelName = (dto.ModelName ?? string.Empty).Trim();
        dto.SystemPrompt ??= string.Empty;
        dto.Personality ??= string.Empty;
        dto.Temperature = Math.Clamp(dto.Temperature, 0, 2);

        // 面板输入框有 min/max，但 API 可以绕过面板直连提交。
        // 输出、输入预算与模型容量分别收敛，至少留出 512 输入与 1024 安全余量。
        dto.MaxTokens = Math.Clamp(dto.MaxTokens, 64, 32768);
        dto.MaxContextMessages = Math.Clamp(dto.MaxContextMessages, 2, 100);
        dto.ContextWindowTokens = Math.Clamp(dto.ContextWindowTokens, 4096, 1048576);
        dto.MaxTokens = Math.Min(dto.MaxTokens, dto.ContextWindowTokens - 1536);
        dto.MaxInputTokens = Math.Clamp(dto.MaxInputTokens, 512, dto.ContextWindowTokens - dto.MaxTokens - 1024);
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

        // 独立搜索服务：数值收敛 + provider 白名单 + URL 限制。
        // null=面板没提交这块（只保存主表单），保持库中原值，不在这里动。
        if (dto.ExternalSearch is { } external)
        {
            external.Provider = (external.Provider ?? string.Empty).Trim();
            if (external.Provider.Length == 0) external.Provider = "searxng";
            if (!ExternalSearchConfig.IsValidProvider(external.Provider))
                return $"未知的搜索服务商：{external.Provider}";
            external.BaseUrl = (external.BaseUrl ?? string.Empty).Trim();
            external.ApiKey = (external.ApiKey ?? string.Empty).Trim();

            // 服务商变更时，空值/旧打码 Key 与旧网关地址都不代表「沿用」：
            // 一律置空，让启用状态下的必填校验生效，避免旧密钥/旧网关被发给新服务商。
            if (currentExternal is { } current
                && !string.Equals(current.Provider, external.Provider, StringComparison.Ordinal))
                ClearStaleExternalSearchSubmit(external, current);

            // 服务商不变但地址 origin 变了：空值/打码值仍代表“沿用旧 Key”，而旧 Key 只属于旧网关。
            // 必须在落库前拒绝，否则一次保存就能把真 Key 绑到新地址（之后的探测会以已保存地址为基准通过校验）。
            if (currentExternal is { } currentTarget && currentTarget.ApiKey.Length > 0
                && string.Equals(currentTarget.Provider, external.Provider, StringComparison.Ordinal)
                && (external.ApiKey.Length == 0 || IsMaskedKey(currentTarget.ApiKey, external.ApiKey)))
            {
                var sameEndpoint = currentTarget.BaseUrl.Trim().Length == 0 && external.BaseUrl.Length == 0
                    || AiConnectionStore.SameOrigin(currentTarget.BaseUrl, external.BaseUrl);
                if (!sameEndpoint)
                    return "搜索地址已变更，请重新填写 API Key 后再保存";
            }

            if (external.BaseUrl.Length > 0)
            {
                // 只放行 http/https，与运行时 RequireHttpUrl 同一条规则
                if (!Uri.TryCreate(external.BaseUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return "搜索服务地址必须是 http/https 开头的完整 URL";
            }

            if (external.Enabled)
            {
                // 地址必填项：SearXNG 要实例根地址，自定义通道要完整端点；
                // 内置商用服务留空时使用官方默认端点。
                if (ExternalSearchConfig.RequiresBaseUrl(external.Provider) && external.BaseUrl.Length == 0)
                    return external.Provider == "custom"
                        ? "自定义搜索通道必须填写服务地址"
                        : "SearXNG 必须填写实例地址";
                // Key 必填项：SearXNG 免 Key，自定义通道选填，其余必须有 Key（打码值视为已配置）。
                // 应用预设时预设本身从不携带外搜 Key，允许先落库再在面板补填。
                if (!allowMissingExternalSearchKey
                    && ExternalSearchConfig.RequiresApiKey(external.Provider) && external.ApiKey.Length == 0)
                    return $"{external.Provider} 需要 API Key";
            }
            external.MaxResults = Math.Clamp(external.MaxResults, 1, ExternalSearchConfig.CeilingMaxResults);
            external.TimeoutSeconds = Math.Clamp(external.TimeoutSeconds, 1, ExternalSearchConfig.CeilingTimeoutSeconds);
        }

        return null;
    }

    /// <summary>
    /// 把已校验的配置写进实体。调用方负责先解析出要落库的 Key。
    /// </summary>
    private void ApplyNormalizedAiConfigInput(AiConfigEntity entity, AiConfigInputDto dto, string apiKey,
        bool applyingPreset = false)
    {
        // 先记住旧目标：下面会立刻把 dto 值写进 entity，再比较就永远相等了。
        var previousBaseUrl = entity.ApiBaseUrl;
        var previousModelName = entity.ModelName;
        var previousConnection = entity.ProviderConnectionId;

        entity.ProviderConnectionId = dto.ProviderConnectionId;
        entity.ApiBaseUrl = dto.ApiBaseUrl;
        entity.ApiKey = apiKey;
        entity.ModelName = dto.ModelName;
        entity.SystemPrompt = dto.SystemPrompt;
        entity.Personality = dto.Personality;
        entity.Temperature = dto.Temperature;
        entity.MaxTokens = dto.MaxTokens;
        entity.MaxContextMessages = dto.MaxContextMessages;
        entity.ContextWindowTokens = dto.ContextWindowTokens;
        entity.MaxInputTokens = dto.MaxInputTokens;
        entity.GroupMemoryInjectionEnabled = dto.GroupMemoryInjectionEnabled;
        entity.MaxAttachedImages = dto.MaxAttachedImages;
        entity.WebSearchEnabled = dto.WebSearchEnabled;
        entity.WebSearchImageMode = dto.WebSearchImageMode;

        var endpoint = dto.WebSearchEndpoint;
        var shape = dto.WebSearchShape;
        var targetBaseUrl = dto.ApiBaseUrl;
        var targetModel = dto.ModelName;

        // 先取本次探测对“提交策略”的结论：刚探测并选用的形状即使目标同时变了也必须保留。
        var probed = shape.Length > 0
            ? _capabilities.GetProbeImageCapability(targetBaseUrl, targetModel, shape, dto.ProviderConnectionId)
            : null;

        // 换了地址或模型，旧策略的验证结论不再可信：除了本次同时选用的新策略，一律清空回退自动推断。
        var targetChanged = !string.Equals(previousBaseUrl, dto.ApiBaseUrl, StringComparison.Ordinal)
                         || previousConnection != dto.ProviderConnectionId
                         || !string.Equals(previousModelName, dto.ModelName, StringComparison.Ordinal);
        if (targetChanged && probed is null &&
            string.Equals(shape, entity.WebSearchShape, StringComparison.Ordinal) &&
            string.Equals(endpoint, entity.WebSearchEndpoint, StringComparison.Ordinal))
        {
            shape = string.Empty;
            endpoint = string.Empty;
        }

        var strategyChanged = !string.Equals(shape, entity.WebSearchShape, StringComparison.Ordinal)
                           || !string.Equals(endpoint, entity.WebSearchEndpoint, StringComparison.Ordinal);

        // 顺序不能反：Clear 会连探测账本一起清掉，先 Clear 再把“刚测出的结论”读成 undefined。
        // 用提交时的形状清理，而不是上面可能已被重置为空的 shape。
        if (targetChanged || strategyChanged)
            _capabilities.Clear(targetBaseUrl, targetModel, dto.WebSearchShape, dto.ProviderConnectionId);

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

        ApplyExternalSearch(entity, dto.ExternalSearch, applyingPreset);
    }

    /// <summary>
    /// 外搜卡片落库。dto 为 null（面板没提交这块）时完全不动库值；
    /// 普通保存按 provider 解析 Key；预设恢复使用调用方已按完整目标解析的 Key（空值也明确写空）。
    /// 服务商/地址/Key 任一变化即清空验证结论。
    /// </summary>
    private static void ApplyExternalSearch(AiConfigEntity entity, ExternalSearchInputDto? dto, bool applyingPreset)
    {
        if (dto is null) return;

        var current = ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson);
        // 双保险：面板路径已在 NormalizeAndValidateAiConfigInput 清过一次；这里兜底
        // 未来绕过校验的写路径，确保旧服务商的 Key/网关不会跟着换 provider 落库。
        if (!applyingPreset && !string.Equals(current.Provider, dto.Provider, StringComparison.Ordinal))
            ClearStaleExternalSearchSubmit(dto, current);
        // SearXNG 免 Key，落库清空，避免旧服务商的 Key 残留；其余服务按归属沿用或覆盖。
        var resolvedKey = applyingPreset
            ? ExternalSearchConfig.SupportsApiKey(dto.Provider) ? dto.ApiKey : string.Empty
            : ResolveProviderAwareApiKey(current.Provider, current.ApiKey, current.BaseUrl,
                dto.Provider, dto.ApiKey, dto.BaseUrl);

        var targetChanged = !string.Equals(current.Provider, dto.Provider, StringComparison.Ordinal)
                            || !string.Equals(current.BaseUrl.TrimEnd('/'), dto.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
                            || !string.Equals(current.ApiKey, resolvedKey, StringComparison.Ordinal);

        entity.ExternalSearchJson = ExternalSearchJsonCodec.Encode(new ExternalSearchConfig
        {
            Enabled = dto.Enabled,
            Provider = dto.Provider,
            BaseUrl = dto.BaseUrl,
            ApiKey = resolvedKey,
            MaxResults = dto.MaxResults,
            TimeoutSeconds = dto.TimeoutSeconds,
            FallbackToBuiltin = dto.FallbackToBuiltin,
            VerifiedAt = targetChanged ? null : current.VerifiedAt
        });
    }

    /// <summary>
    /// 表单里的 Key 为空或仍是打码值（说明用户没改），保持数据库中的原 Key。
    /// </summary>
    private static string ResolveSubmittedApiKey(string? currentKey, string? submittedKey)
    {
        currentKey ??= string.Empty;
        if (string.IsNullOrWhiteSpace(submittedKey) || IsMaskedKey(currentKey, submittedKey))
            return currentKey;
        return submittedKey;
    }

    /// <summary>
    /// 是否为当前 Key 的“打码值”。接受完整掩码与旧版 4+****+4 打码，
    /// 避免升级前打开的旧页面把打码文本当成新 Key 写回。
    /// </summary>
    private static bool IsMaskedKey(string currentKey, string submittedKey) =>
        AiConnectionStore.IsMaskedSecret(currentKey, submittedKey);

    /// <summary>
    /// 按 provider 与地址归属解析提交的外搜 Key（纯函数，供落库/探测与自检复用）：
    /// 同一服务商且同一 origin 时空值/打码值=沿用已存 Key；换了服务商、或地址 origin 变了，
    /// 空值与打码值一律解析为空（必填校验会要求重填），绝不把已存 Key 发给改后的网关；
    /// SearXNG 免 Key，恒为空。
    /// </summary>
    internal static string ResolveProviderAwareApiKey(
        string? currentProvider, string? currentKey, string? currentBaseUrl,
        string? submittedProvider, string? submittedKey, string? submittedBaseUrl)
    {
        currentKey ??= string.Empty;
        submittedKey ??= string.Empty;
        if (!ExternalSearchConfig.SupportsApiKey(submittedProvider))
            return string.Empty;
        var sameProvider = string.Equals(currentProvider ?? string.Empty, submittedProvider ?? string.Empty, StringComparison.Ordinal);
        if (!sameProvider)
        {
            if (string.IsNullOrWhiteSpace(submittedKey)
                || IsMaskedKey(currentKey, submittedKey))
                return string.Empty;
            return submittedKey;
        }
        if (!string.IsNullOrWhiteSpace(submittedKey) && !IsMaskedKey(currentKey, submittedKey))
            return submittedKey;
        // 沿用已存 Key 时地址必须是保存时的 origin：否则一次保存就能把真 Key 绑到攻击者网关，
        // 之后的探测会因为“当前地址”已被改掉而通过 origin 校验。
        var sameEndpoint = string.IsNullOrWhiteSpace(currentBaseUrl) && string.IsNullOrWhiteSpace(submittedBaseUrl)
            || AiConnectionStore.SameOrigin(currentBaseUrl ?? string.Empty, submittedBaseUrl ?? string.Empty);
        return sameEndpoint ? currentKey : string.Empty;
    }

    /// <summary>
    /// 服务商变更时清掉「沿用旧服务商」的提交（就地修改，纯函数可测）：
    /// Key 为空或等于旧 Key 打码值 → 置空（启用时的必填校验会要求重填）；
    /// 地址与旧网关相同（TrimEnd('/') 比较）→ 置空（官方端点自动用默认，避免商用服务打到旧网关）。
    /// provider 未变时不做任何事。
    /// </summary>
    internal static void ClearStaleExternalSearchSubmit(ExternalSearchInputDto submitted, ExternalSearchConfig current)
    {
        if (string.IsNullOrWhiteSpace(submitted.ApiKey)
            || IsMaskedKey(current.ApiKey, submitted.ApiKey))
            submitted.ApiKey = string.Empty;
        if (submitted.BaseUrl.TrimEnd('/').Length > 0
            && string.Equals(current.BaseUrl.TrimEnd('/'), submitted.BaseUrl.TrimEnd('/'), StringComparison.Ordinal))
            submitted.BaseUrl = string.Empty;
    }

    private static ExternalSearchInputDto ToExternalSearchInput(ExternalSearchConfig config) => new()
    {
        Enabled = config.Enabled ?? false,
        Provider = config.Provider,
        BaseUrl = config.BaseUrl,
        // 预设 JSON 不落外搜 Key；读取预设时也不回显其真值。
        ApiKey = string.Empty,
        MaxResults = config.MaxResults,
        TimeoutSeconds = config.TimeoutSeconds,
        FallbackToBuiltin = config.FallbackToBuiltin
    };

    private static AiPresetConfigDto SnapshotAiConfig(AiConfigEntity? entity)
    {
        if (entity == null) return new AiPresetConfigDto();

        return new AiPresetConfigDto
        {
            Version = entity.ProviderConnectionId.Length > 0 ? 2 : 1,
            ProviderConnectionId = entity.ProviderConnectionId,
            ApiBaseUrl = entity.ApiBaseUrl,
            ModelName = entity.ModelName,
            SystemPrompt = entity.SystemPrompt,
            Personality = entity.Personality,
            Temperature = entity.Temperature,
            MaxTokens = entity.MaxTokens,
            MaxContextMessages = entity.MaxContextMessages,
            ContextWindowTokens = entity.ContextWindowTokens,
            MaxInputTokens = entity.MaxInputTokens,
            GroupMemoryInjectionEnabled = entity.GroupMemoryInjectionEnabled,
            MaxAttachedImages = entity.MaxAttachedImages,
            WebSearchEnabled = entity.WebSearchEnabled,
            WebSearchEndpoint = entity.WebSearchEndpoint,
            WebSearchShape = entity.WebSearchShape,
            WebSearchImageMode = string.IsNullOrWhiteSpace(entity.WebSearchImageMode)
                ? AiConfig.WebSearchImageModeAuto
                : entity.WebSearchImageMode,
            ExternalSearch = ToExternalSearchInput(ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson))
        };
    }

    private static AiPresetConfigDto ToPresetConfig(AiConfigInputDto input) => new()
    {
        Version = input.ProviderConnectionId.Length > 0 ? 2 : 1,
        ProviderConnectionId = input.ProviderConnectionId,
        ProviderConnectionVersion = input.ProviderConnectionVersion,
        ApiBaseUrl = input.ApiBaseUrl,
        ModelName = input.ModelName,
        SystemPrompt = input.SystemPrompt,
        Personality = input.Personality,
        Temperature = input.Temperature,
        MaxTokens = input.MaxTokens,
        MaxContextMessages = input.MaxContextMessages,
        ContextWindowTokens = input.ContextWindowTokens,
        MaxInputTokens = input.MaxInputTokens,
        GroupMemoryInjectionEnabled = input.GroupMemoryInjectionEnabled,
        MaxAttachedImages = input.MaxAttachedImages,
        WebSearchEnabled = input.WebSearchEnabled,
        WebSearchEndpoint = input.WebSearchEndpoint,
        WebSearchShape = input.WebSearchShape,
        WebSearchImageMode = input.WebSearchImageMode,
        ExternalSearch = input.ExternalSearch
    };

    private static string SerializePresetConfig(AiPresetConfigDto config)
    {
        // 双保险：主 Key 只走单独一列；外搜 Key 不进入预设快照，仅在应用到相同目标时继承。
        config.Version = config.ProviderConnectionId.Length > 0 ? 2 : 1;
        config.ApiKey = string.Empty;
        if (config.ExternalSearch is not null)
            config.ExternalSearch.ApiKey = string.Empty;
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
        // 重复键会让 STJ 抛 ArgumentException 而不是 JsonException：同样按损坏处理，不让详情/应用 500。
        catch (Exception ex) when (ex is JsonException or ArgumentException)
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
            ProviderConnectionId = config.ProviderConnectionId,
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
            // 外搜卡片信息供应用确认时提示「需重填搜索 Key」；快照本身不含外搜 Key
            ExternalSearchEnabled = config.ExternalSearch?.Enabled ?? false,
            ExternalSearchProvider = config.ExternalSearch?.Provider ?? string.Empty,
            ExternalSearchBaseUrl = config.ExternalSearch?.BaseUrl ?? string.Empty,
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
        // 双保险：旧数据里若残留外搜 Key，详情接口也不回显。
        if (config.ExternalSearch is not null) config.ExternalSearch.ApiKey = string.Empty;

        return new AiPresetDetailDto
        {
            ProviderConnectionId = summary.ProviderConnectionId,
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
            ExternalSearchEnabled = summary.ExternalSearchEnabled,
            ExternalSearchProvider = summary.ExternalSearchProvider,
            ExternalSearchBaseUrl = summary.ExternalSearchBaseUrl,
            HasApiKey = summary.HasApiKey,
            IsValid = summary.IsValid,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            Config = config,
            // 已绑定的预设从连接取 Key：旧列里的残留副本连部分打码都不回显。
            // 未绑定的预设也只回显完整掩码，避免泄露首尾字符。
            ApiKey = summary.ProviderConnectionId.Length > 0 || entity.ApiKey.Length == 0
                ? string.Empty : AiConnectionStore.SecretMask
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
    /// 「测试搜索」：用表单当前值跑一次真实外搜（SearXNG/官方端点均真请求），
    /// 一次成败即结论，不需要形状矩阵。Key 打码时回退库中真值，与探测同一规则。
    /// 成功会顺手把 VerifiedAt 落库。
    /// </summary>
    [HttpPost("ai/probe-search")]
    public async Task<IActionResult> ProbeExternalSearch(
        [FromBody] ExternalSearchInputDto dto,
        [FromServices] ExternalSearchService searchService,
        CancellationToken ct)
    {
        dto.Provider = (dto.Provider ?? string.Empty).Trim();
        if (dto.Provider.Length == 0) dto.Provider = "searxng";
        if (!ExternalSearchConfig.IsValidProvider(dto.Provider))
            return BadRequest(new { error = $"未知的搜索服务商：{dto.Provider}" });
        dto.BaseUrl = (dto.BaseUrl ?? string.Empty).Trim();
        dto.ApiKey = (dto.ApiKey ?? string.Empty).Trim();
        dto.MaxResults = Math.Clamp(dto.MaxResults, 1, ExternalSearchConfig.CeilingMaxResults);
        dto.TimeoutSeconds = Math.Clamp(dto.TimeoutSeconds, 1, ExternalSearchConfig.CeilingTimeoutSeconds);

        // 探测也要求配置完整：地址必填项与 Key 必填项按服务商能力判断。
        // Key 按 provider 归属解析：同一服务商时空值/打码值=沿用已存 Key；
        // 换了服务商时空值或旧打码值/旧网关地址一律视为未提交，由 IsConfigured 返回 400 提示重填。
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync(ct);
        var current = ExternalSearchJsonCodec.Decode(entity?.ExternalSearchJson);
        if (!string.Equals(current.Provider, dto.Provider, StringComparison.Ordinal))
            ClearStaleExternalSearchSubmit(dto, current);
        // 沿用已保存的搜索 Key 时，地址必须仍是保存时的 origin，避免把 Key 发往任意网关。
        // 换了服务商时 Key 已经被清空、根本不会沿用，不能再报“地址变更”拦下免 Key 的 SearXNG/自定义通道。
        var sameProvider = string.Equals(current.Provider, dto.Provider, StringComparison.Ordinal);
        var submittedKey = dto.ApiKey ?? string.Empty;
        var reusesSavedKey = submittedKey.Length == 0
            || AiConnectionStore.IsMaskedSecret(current.ApiKey, submittedKey);
        var sameEndpoint = string.IsNullOrWhiteSpace(current.BaseUrl) && string.IsNullOrWhiteSpace(dto.BaseUrl)
            || AiConnectionStore.SameOrigin(current.BaseUrl, dto.BaseUrl);
        if (sameProvider && reusesSavedKey && current.ApiKey.Length > 0 && !sameEndpoint)
            return BadRequest(new { error = "搜索地址已变更，请重新填写 API Key 后再测试" });
        var apiKey = ResolveProviderAwareApiKey(current.Provider, current.ApiKey, current.BaseUrl,
            dto.Provider, dto.ApiKey, dto.BaseUrl);

        var probeConfig = new AiConfig
        {
            // 外搜不依赖模型端点；WebSearchEnabled 在 ExternalSearchService 内部以卡片为准
            WebSearchEnabled = true,
            ExternalSearch = new ExternalSearchConfig
            {
                Enabled = true,
                Provider = dto.Provider,
                BaseUrl = dto.BaseUrl,
                ApiKey = apiKey,
                MaxResults = dto.MaxResults,
                TimeoutSeconds = dto.TimeoutSeconds,
                FallbackToBuiltin = false
            }
        };

        if (!ExternalSearchConfig.IsConfigured(probeConfig.ExternalSearch.Normalized()))
            return BadRequest(new
            {
                error = ExternalSearchConfig.RequiresBaseUrl(dto.Provider)
                    ? (dto.Provider == "custom"
                        ? "自定义搜索通道需要填写服务地址"
                        : "SearXNG 需要填写实例地址")
                    : $"{dto.Provider} 需要填写 API Key"
            });

        // 探测绕过 3 分钟缓存：直接走 provider，避免「刚才测过」干扰判断
        var provider = searchService.ResolveProvider(dto.Provider);
        if (provider is null)
            return BadRequest(new { error = $"未知的搜索服务商：{dto.Provider}" });

        WebSearchOutcome outcome;
        WebSearchFailure? failure = null;
        try
        {
            outcome = await provider.SearchAsync(
                probeConfig.ExternalSearch.Normalized(), ExternalSearchProbe.TestQuery, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = WebSearchOutcome.Empty;
            failure = ex is WebSearchRequestException typed
                ? new WebSearchFailure(typed.Kind, typed.HttpStatus, typed.Message)
                : new WebSearchFailure(WebSearchFailureKind.Transient, null, ex.Message);
        }
        var (verdict, detail) = ExternalSearchProbe.Judge(outcome, failure, dto.Provider);

        // verified 时把结论顺手落库（与表单一致的值），面板刷新即见“已验证”
        if (verdict == "verified" && entity is not null)
        {
            var saved = ExternalSearchJsonCodec.Decode(entity.ExternalSearchJson);
            // SearXNG 免 Key：库里可能残留旧服务商 Key，比对时忽略，避免验证时间永远无法记录
            var savedKey = ExternalSearchConfig.SupportsApiKey(saved.Provider) ? saved.ApiKey : string.Empty;
            var matchesForm = string.Equals(saved.Provider, dto.Provider, StringComparison.Ordinal)
                              && string.Equals(saved.BaseUrl.TrimEnd('/'), dto.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
                              && string.Equals(savedKey, apiKey, StringComparison.Ordinal);
            if (matchesForm)
            {
                entity.ExternalSearchJson = ExternalSearchJsonCodec.Encode(saved with { VerifiedAt = DateTime.UtcNow });
                await _dbContext.SaveChangesAsync(ct);
            }
        }

        return Ok(new ExternalSearchProbeResultDto
        {
            Verdict = verdict,
            Detail = detail,
            Hits = outcome.Hits.Take(3).Select(h => new ExternalSearchHitDto
            {
                Title = h.Title,
                Url = h.Url
            }).ToList()
        });
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

        var (apiKey, keyError) = await ResolveProbeApiKeyAsync(dto);
        if (apiKey is null)
            return BadRequest(new { error = keyError ?? "尚未保存过 API Key，请先填写并保存配置再探测" });

        WebSearchProbeReportDto? report = null;
        WebSearchProbeEventDto? failure = null;
        await foreach (var ev in RunProbeEventsAsync(apiBaseUrl, apiKey, model, runner, ct, dto.ProviderConnectionId ?? ""))
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

        var (apiKey, keyError) = await ResolveProbeApiKeyAsync(dto);
        if (apiKey is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = keyError ?? "尚未保存过 API Key，请先填写并保存配置再探测" }, ct);
            return;
        }

        // 逐行 JSON，前端用 fetch + ReadableStream 解析；关闭中间层缓冲，保证逐项到达
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            await foreach (var ev in RunProbeEventsAsync(apiBaseUrl, apiKey, model, runner, ct, dto.ProviderConnectionId ?? ""))
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

    /// <summary>
    /// Key 传打码值说明表单没改：探测必须用真 Key，从库里取。
    /// 沿用已保存 Key 时目标必须仍是保存时的 origin，否则打码值会把密钥带去任意主机。
    /// </summary>
    private async Task<(string? Key, string? Error)> ResolveProbeApiKeyAsync(WebSearchProbeDto dto)
    {
        var connectionId = dto.ProviderConnectionId ?? string.Empty;
        if (connectionId.Length > 0)
        {
            var connection = await _dbContext.AiProviderConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == connectionId);
            if (connection == null) return (null, "选中的服务商连接已不存在，请重新选择");
            if (!AiConnectionStore.SameOrigin(connection.ApiBaseUrl, dto.ApiBaseUrl))
                return (null, "探测地址与所选连接不一致，请先保存连接或重新填写 API Key");
            if (AiConnectionStore.Models(connection).All(m => m.Id != dto.ModelName))
                return (null, "该连接下没有选中模型，请先在管理服务商中添加模型");
            return (connection.ApiKey, null);
        }
        var apiKey = dto.ApiKey;
        var entity = await _dbContext.AiConfigs.FirstOrDefaultAsync();
        var saved = entity?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || AiConnectionStore.IsMaskedSecret(saved, apiKey))
        {
            if (string.IsNullOrWhiteSpace(saved)) return (null, "尚未保存过 API Key，请先填写并保存配置再探测");
            if (entity != null && !AiConnectionStore.SameOrigin(entity.ApiBaseUrl, dto.ApiBaseUrl))
                return (null, "探测地址与已保存配置不一致，请重新填写 API Key");
            return (saved, null);
        }
        return (apiKey, null);
    }

    /// <summary>
    /// 探测的单一执行路径：一次性接口与流式接口都消费这里，避免两套逻辑漂移。
    /// </summary>
    private async IAsyncEnumerable<WebSearchProbeEventDto> RunProbeEventsAsync(
        string apiBaseUrl, string apiKey, string model,
        Mutter.Core.Services.WebSearchProbeRunner runner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string providerConnectionId = "")
    {
        var connectionProtocol = string.Empty;
        if (providerConnectionId.Length > 0)
        {
            var connection = await _dbContext.AiProviderConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == providerConnectionId, ct);
            if (connection != null)
            {
                // 探测与正式请求必须同模板、同超时，否则会出现“探测通过、线上 400”的假阴性。
                runner = runner.WithConnection(AiConnectionStore.Headers(connection),
                    connection.TemplateId, connection.TimeoutSeconds);
                connectionProtocol = connection.Protocol;
            }
        }
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
            // Gemini 原生连接可能根本没挂 OpenAI 兼容层：以 gemini 基线的结论为准，不在这里终止。
            if (connectionProtocol != "gemini")
            {
                yield return new WebSearchProbeEventDto
                {
                    Type = "error", Reachable = true, AuthFailed = true,
                    Error = $"鉴权失败（{baseStatus}）：Key 无效或无权限，搜索结论无从谈起"
                };
                yield break;
            }
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
                // 两个通道都拒绝鉴权时才是真的 Key 无效。
                if (gs is 401 or 403 && baseStatus is 401 or 403)
                {
                    yield return new WebSearchProbeEventDto
                    {
                        Type = "error", Reachable = true, AuthFailed = true,
                        Error = "鉴权失败：Key 无效或无权限，搜索结论无从谈起"
                    };
                    yield break;
                }
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
                        imageCapable: imageVerdict.Status == "verified", connectionId: providerConnectionId);
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

        // SQLite 的 ==/LIKE 默认区分大小写，而人设标识按 OrdinalIgnoreCase 语义管理：
        // 先取出带人设的行，在内存中做大小写不敏感比较，避免大小写变体残留引用。
        var groupConfigs = await _dbContext.GroupConfigs
            .Where(g => g.ActiveCharacter != null)
            .ToListAsync();
        foreach (var g in groupConfigs.Where(g =>
                     string.Equals(g.ActiveCharacter, name, StringComparison.OrdinalIgnoreCase)))
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

/// <summary>「测试搜索」结论：一次真实检索的成败即结果。</summary>
public class ExternalSearchProbeResultDto
{
    /// <summary>verified / no_results / auth_failed / failed。</summary>
    public string Verdict { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public List<ExternalSearchHitDto> Hits { get; set; } = new();
}

public class ExternalSearchHitDto
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class WebSearchProbeDto
{
    public string ProviderConnectionId { get; set; } = string.Empty;
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
