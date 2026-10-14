namespace Mutter.Core.Services;

/// <summary>
/// 机器人群内 @ 判定。开启群全量消息后，QQ 不再单独下发 GROUP_AT_MESSAGE_CREATE，
/// 只把消息塞进 GROUP_MESSAGE_CREATE，事件类型里"@ 了 bot"这个信息就此丢失 ——
/// 线上表现是围观档下 @ 它完全不理。好在 content 里仍带着机器人群内识别码的 at 段
/// （形如 &lt;@36CD…&gt;），只能靠它判。识别码由面板按群配置：它是 QQ 下发的 openid，
/// 不能写死在代码里。
/// </summary>
public static class BotMention
{
    public static bool IsMentioned(string? content, string? code)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(code)) return false;
        return content.Contains($"<@{code}>", StringComparison.OrdinalIgnoreCase)
            || content.Contains($"<@!{code}>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 去掉 at 机器人自己的那一段，交给 LLM 的是纯问题文本
    /// （官方 @ 事件下发的 content 本来就是去掉前缀的，这里保持一致）。
    /// </summary>
    public static string Strip(string content, string? code)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(code)) return content;
        return content
            .Replace($"<@!{code}>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace($"<@{code}>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
