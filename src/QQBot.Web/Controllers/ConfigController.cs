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
        var characters = AiCharacter.GetDefaults().Select(c => new CharacterDto
        {
            Name = c.Name,
            DisplayName = c.DisplayName,
            Description = c.Description
        }).ToList();

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
        entity.MaxTokens = dto.MaxTokens;
        entity.MaxContextMessages = dto.MaxContextMessages;
        entity.WebSearchEnabled = dto.WebSearchEnabled;

        await _dbContext.SaveChangesAsync();
        return NoContent();
    }

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
