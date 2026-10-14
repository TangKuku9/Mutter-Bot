using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QQBot.Core.Configuration;
using QQBot.Core.Interfaces;
using QQBot.Core.Models.Config;
using QQBot.Infrastructure.Data;
using QQBot.Infrastructure.Entities;
using QQBot.Web.Models;

namespace QQBot.Web.Controllers;

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

        await _dbContext.SaveChangesAsync();
        return NoContent();
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
        [FromServices] IOptions<QQBotOptions> options)
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
