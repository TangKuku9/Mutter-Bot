using System.Text.Json;

namespace QQBot.Core.Game.Models;

/// <summary>
/// 渡劫护法会话。玩家发起突破后创建，10分钟内其他群友可护法增加成功率。
/// 存储为 JSON 在 GroupWorld.TribulationSessionJson 中。
/// </summary>
public class TribulationSession
{
    public string MemberOpenId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
    public List<string> Protectors { get; set; } = new();

    /// <summary>每个护法提供的成功率加成（5%），上限5人。</summary>
    public const double ProtectorBonus = 0.05;
    public const int MaxProtectors = 5;

    public double TotalBonus => Math.Min(Protectors.Count, MaxProtectors) * ProtectorBonus;
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    public bool CanAddProtector(string memberOpenId) =>
        !IsExpired
        && memberOpenId != MemberOpenId
        && !Protectors.Contains(memberOpenId)
        && Protectors.Count < MaxProtectors;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static string Serialize(TribulationSession? session) =>
        session is null ? string.Empty : JsonSerializer.Serialize(session, JsonOpts);

    public static TribulationSession? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<TribulationSession>(json, JsonOpts);
}
