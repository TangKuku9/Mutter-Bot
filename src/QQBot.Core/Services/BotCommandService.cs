using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Services;

public class BotCommandService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IQqBotApiService _apiService;
    private readonly GameService _gameService;
    private readonly ILogger<BotCommandService> _logger;

    private const string HelpText =
        """
        Available commands:
        /help - Show this help
        /status - Bot status
        /groups - List all groups
        /group on <groupOpenId> - Enable group
        /group off <groupOpenId> - Disable group
        /private on <userOpenId> - Allow private chat
        /private off <userOpenId> - Block private chat
        /char list - List available characters
        /char set <name> - Switch character (admin only)
        /char reset - Reset to default character
        /quiz start <type> - Start quiz (riddle/idiom/brain teaser)
        /quiz answer <answer> - Submit answer
        /quiz stop - Stop current game
        """;

    public BotCommandService(
        IServiceScopeFactory scopeFactory,
        IQqBotApiService apiService,
        GameService gameService,
        ILogger<BotCommandService> logger)
    {
        _scopeFactory = scopeFactory;
        _apiService = apiService;
        _gameService = gameService;
        _logger = logger;
    }

    public static bool LooksLikeCommand(string text) =>
        text.TrimStart().StartsWith('/');

    public async Task<string> ExecuteAsync(string senderId, string text, CancellationToken ct = default)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        try
        {
            return cmd switch
            {
                "/help" => HelpText,
                "/status" => await BuildStatusAsync(ct),
                "/groups" => await BuildGroupListAsync(ct),
                "/group" => await ToggleGroupAsync(parts, ct),
                "/private" => await TogglePrivateAsync(senderId, parts, ct),
                "/char" => await HandleCharacterAsync(senderId, parts, ct),
                "/quiz" => await HandleQuizAsync(senderId, parts, ct),
                _ => $"Unknown command {cmd}. Send /help for available commands."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command failed: {Command}", text);
            return "Command execution failed. Please try again.";
        }
    }

    public async Task<string> ExecuteGroupCommandAsync(string senderId, string text, string groupId, string? senderRole, CancellationToken ct = default)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        var isAdmin = senderRole is "owner" or "admin";

        try
        {
            return cmd switch
            {
                "/char" => await HandleCharacterGroupAsync(senderId, parts, groupId, isAdmin, ct),
                "/quiz" => await HandleQuizGroupAsync(senderId, parts, groupId, isAdmin, ct),
                _ => $"Unknown command {cmd}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Group command failed: {Command}", text);
            return "Command execution failed.";
        }
    }

    private async Task<string> BuildStatusAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageRepo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
        var total = await messageRepo.CountMessagesAsync(null, ct);
        var today = await messageRepo.CountMessagesAsync(DateTime.Today.ToUniversalTime(), ct);
        var aiCalls = await messageRepo.CountAiCallsAsync(ct);
        var tokens = await messageRepo.SumTokensAsync(ct);

        return $"""
            Bot Status
            Uptime: {(int)uptime.TotalHours}h {uptime.Minutes}m
            Total messages: {total}
            Today: {today}
            AI calls: {aiCalls}
            Tokens used: {tokens}
            """;
    }

    private async Task<string> BuildGroupListAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var groups = await configRepo.GetKnownGroupsAsync(ct);
        if (groups.Count == 0) return "No groups found.";

        var configs = await configRepo.GetAllGroupConfigsAsync(ct);
        var enabledSet = configs.Where(c => c.IsEnabled).Select(c => c.GroupId).ToHashSet();

        var sb = new StringBuilder("Groups:\n");
        foreach (var g in groups)
        {
            sb.AppendLine($"{(enabledSet.Contains(g.GroupId) ? "[ON]" : "[OFF]")} {g.GroupName} ({g.GroupId})");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ToggleGroupAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3 || (parts[1] != "on" && parts[1] != "off"))
            return "Usage: /group on|off <groupOpenId>";

        var groupId = parts[2];
        var enable = parts[1] == "on";

        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        var config = await configRepo.GetGroupConfigAsync(groupId, ct) ?? new GroupConfig { GroupId = groupId };
        config.IsEnabled = enable;
        await configRepo.SaveGroupConfigAsync(config, ct);

        return $"Group {groupId} {(enable ? "enabled" : "disabled")}";
    }

    private async Task<string> TogglePrivateAsync(string senderId, string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3 || (parts[1] != "on" && parts[1] != "off"))
            return "Usage: /private on|off <userOpenId>";

        var userId = parts[2];
        var enable = parts[1] == "on";

        if (!enable && userId == senderId)
            return "Cannot disable your own private chat.";

        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        await configRepo.SetPrivateChatEnabledAsync(userId, enable, ct);

        return $"User {userId} private chat {(enable ? "allowed" : "blocked")}";
    }

    private Task<string> HandleCharacterAsync(string senderId, string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2) return Task.FromResult("Usage: /char list|set <name>|reset");

        var subCmd = parts[1].ToLowerInvariant();
        return Task.FromResult(subCmd switch
        {
            "list" => BuildCharacterList(),
            _ => "Use /char list to see available characters."
        });
    }

    private async Task<string> HandleCharacterGroupAsync(string senderId, string[] parts, string groupId, bool isAdmin, CancellationToken ct)
    {
        if (parts.Length < 2) return "Usage: /char list|set <name>|reset";

        var subCmd = parts[1].ToLowerInvariant();
        return subCmd switch
        {
            "list" => BuildCharacterList(),
            "set" => await SetCharacterAsync(parts, groupId, isAdmin, ct),
            "reset" => await ResetCharacterAsync(groupId, isAdmin, ct),
            _ => "Usage: /char list|set <name>|reset"
        };
    }

    private static string BuildCharacterList()
    {
        var chars = AiCharacter.GetDefaults();
        var sb = new StringBuilder("Available characters:\n");
        foreach (var c in chars)
        {
            sb.AppendLine($"  {c.Name} - {c.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> SetCharacterAsync(string[] parts, string groupId, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return "Only group admins can change characters.";
        if (parts.Length < 3) return "Usage: /char set <name>";

        var charName = parts[2];
        var character = AiCharacter.GetDefaults().FirstOrDefault(c =>
            string.Equals(c.Name, charName, StringComparison.OrdinalIgnoreCase));
        if (character == null) return $"Unknown character: {charName}. Use /char list to see available characters.";

        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var config = await configRepo.GetGroupConfigAsync(groupId, ct) ?? new GroupConfig { GroupId = groupId, IsEnabled = true };
        config.ActiveCharacter = character.Name;
        await configRepo.SaveGroupConfigAsync(config, ct);

        return $"Character set to: {character.Name} ({character.Description})";
    }

    private async Task<string> ResetCharacterAsync(string groupId, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return "Only group admins can change characters.";

        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var config = await configRepo.GetGroupConfigAsync(groupId, ct) ?? new GroupConfig { GroupId = groupId, IsEnabled = true };
        config.ActiveCharacter = null;
        await configRepo.SaveGroupConfigAsync(config, ct);

        return "Character reset to default.";
    }

    private Task<string> HandleQuizAsync(string senderId, string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2) return Task.FromResult("Usage: /quiz start <type>|answer <answer>|stop");
        return Task.FromResult("Quiz commands are only available in groups.");
    }

    private async Task<string> HandleQuizGroupAsync(string senderId, string[] parts, string groupId, bool isAdmin, CancellationToken ct)
    {
        if (parts.Length < 2) return "Usage: /quiz start <type>|answer <answer>|stop";

        var subCmd = parts[1].ToLowerInvariant();
        return subCmd switch
        {
            "start" => isAdmin
                ? await _gameService.StartGameAsync(groupId, string.Join(' ', parts.Skip(2)))
                : "Only group admins can start games.",
            "answer" => parts.Length >= 3
                ? await _gameService.SubmitAnswerAsync(groupId, string.Join(' ', parts.Skip(2)))
                : "Usage: /quiz answer <your answer>",
            "stop" => isAdmin
                ? _gameService.StopGame(groupId)
                : "Only group admins can stop games.",
            _ => "Usage: /quiz start <type>|answer <answer>|stop"
        };
    }
}
