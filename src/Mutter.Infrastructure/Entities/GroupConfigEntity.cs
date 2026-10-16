namespace Mutter.Infrastructure.Entities;

public class GroupConfigEntity
{
    public string GroupId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    /// <summary>0=只回被 @ 的消息；2=围观全部消息并落库。</summary>
    public int TriggerMode { get; set; }
    public string? CustomSystemPrompt { get; set; }
    public double? CustomTemperature { get; set; }

    /// <summary>本群是否参与 AI 择机发言（仍需全局开关同时打开）。</summary>
    public bool AiProactiveEnabled { get; set; }

    /// <summary>机器人在本群的 @ 识别码，见 <see cref="Mutter.Core.Models.Config.GroupConfig.BotMentionOpenId"/>。</summary>
    public string? BotMentionOpenId { get; set; }

    public string? ActiveCharacter { get; set; }

    /// <summary>群级群记忆注入覆盖（null = 跟随全局），见 <see cref="Mutter.Core.Models.Config.GroupConfig.MemoryInjectionEnabled"/>。</summary>
    public bool? MemoryInjectionEnabled { get; set; }
}
