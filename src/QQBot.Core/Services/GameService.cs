using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core.Interfaces;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Services;

public class GameService
{
    private readonly ConcurrentDictionary<string, GameState> _activeGames = new();
    private readonly IAiChatService _aiService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GameService> _logger;

    private static readonly TimeSpan GameExpiry = TimeSpan.FromMinutes(30);

    public GameService(IAiChatService aiService, IServiceScopeFactory scopeFactory, ILogger<GameService> logger)
    {
        _aiService = aiService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<string> StartGameAsync(string groupId, string gameType)
    {
        var newState = new GameState
        {
            GroupId = groupId,
            StartedAt = DateTime.UtcNow,
            IsAnswered = false
        };

        if (!_activeGames.TryAdd(groupId, newState))
            return "This group already has an active game. Use /quiz stop to end it first.";

        var validTypes = new[] { "riddle", "idiom", "brain teaser" };
        var matchedType = validTypes.FirstOrDefault(t =>
            gameType.Contains(t, StringComparison.OrdinalIgnoreCase))
            ?? validTypes.FirstOrDefault(t =>
                t.Contains(gameType, StringComparison.OrdinalIgnoreCase));

        if (matchedType == null)
        {
            _activeGames.TryRemove(groupId, out _);
            return $"Invalid game type. Available: {string.Join(", ", validTypes)}";
        }

        try
        {
            // Load real AI config from database
            using var scope = _scopeFactory.CreateScope();
            var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            var aiConfig = await configRepo.GetAiConfigAsync() ?? new AiConfig();
            aiConfig.Temperature = 0.8;
            aiConfig.MaxTokens = 500;

            var systemPrompt = $"You are a quiz game host. Generate ONE {matchedType} question. " +
                "Reply in EXACTLY this format:\n" +
                "Question: <your question>\n" +
                "Answer: <the correct answer>\n" +
                "Do NOT include anything else.";

            var response = await _aiService.ChatAsync(
                systemPrompt,
                new List<(string, string)> { ("user", $"Generate a {matchedType} question in Chinese.") },
                aiConfig);

            var content = response.Content;
            var question = "";
            var answer = "";

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Question:", StringComparison.OrdinalIgnoreCase))
                {
                    question = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                }
                else if (trimmed.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase))
                {
                    answer = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                }
            }

            if (string.IsNullOrEmpty(question) || string.IsNullOrEmpty(answer))
            {
                _activeGames.TryRemove(groupId, out _);
                return "Failed to generate question. Please try again.";
            }

            // Update the state that was already added to the dictionary
            newState.GameType = matchedType;
            newState.Question = question;
            newState.CorrectAnswer = answer;

            return $"Quiz time! ({matchedType})\n\n{question}\n\nType your answer with: /quiz answer <your answer>";
        }
        catch (Exception ex)
        {
            _activeGames.TryRemove(groupId, out _);
            _logger.LogError(ex, "Failed to generate quiz for group {GroupId}", groupId);
            return "Failed to generate question. Please try again.";
        }
    }

    public Task<string> SubmitAnswerAsync(string groupId, string answer)
    {
        // Clean up expired games
        CleanupExpiredGames();

        if (!_activeGames.TryGetValue(groupId, out var state))
            return Task.FromResult("No active game. Use /quiz start <type> to start one.");

        bool alreadyAnswered;
        lock (state)
        {
            alreadyAnswered = state.IsAnswered;
            if (!alreadyAnswered)
                state.IsAnswered = true;
        }

        if (alreadyAnswered)
            return Task.FromResult("This question has already been answered!");

        var isCorrect = answer.Trim().Equals(state.CorrectAnswer.Trim(), StringComparison.OrdinalIgnoreCase);

        _activeGames.TryRemove(groupId, out _);

        if (isCorrect)
            return Task.FromResult($"Correct! The answer is: {state.CorrectAnswer}\n\nGreat job!");
        else
            return Task.FromResult($"Wrong! The correct answer was: {state.CorrectAnswer}");
    }

    public string StopGame(string groupId)
    {
        if (_activeGames.TryRemove(groupId, out var state))
            return $"Game stopped. The answer was: {state.CorrectAnswer}";
        return "No active game to stop.";
    }

    private void CleanupExpiredGames()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _activeGames)
        {
            if (now - kvp.Value.StartedAt > GameExpiry)
            {
                _activeGames.TryRemove(kvp.Key, out _);
                _logger.LogDebug("Cleaned up expired game for group {GroupId}", kvp.Key);
            }
        }
    }
}

public class GameState
{
    public string GroupId { get; set; } = string.Empty;
    public string GameType { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string CorrectAnswer { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public bool IsAnswered { get; set; }
}
