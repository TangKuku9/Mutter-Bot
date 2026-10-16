namespace Mutter.Core.Models.Config;

public class GroupConfig
{
    public string GroupId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? CustomSystemPrompt { get; set; }
    public double? CustomTemperature { get; set; }

    /// <summary>0=只回被 @ 的消息；2=围观全部消息并落库。</summary>
    public int TriggerMode { get; set; }

    /// <summary>本群是否参与 AI 择机发言（仍需全局开关同时打开）。</summary>
    public bool AiProactiveEnabled { get; set; }

    /// <summary>
    /// 机器人在本群的 @ 识别码（QQ 下发的 member openid）。全量模式下平台不再单独下发
    /// @ 事件，只能靠 content 里的 &lt;@识别码&gt; 判断有没有人 @ 机器人；留空则完全不判。
    /// </summary>
    public string? BotMentionOpenId { get; set; }

    public string? ActiveCharacter { get; set; }

    /// <summary>
    /// 本群是否允许把群记忆注入择机判定。null = 跟随全局。
    /// 只控制"用不用"，不控制"存不存"：设成 false 后摘要仍在，随时能改回来。
    /// </summary>
    public bool? MemoryInjectionEnabled { get; set; }
}
