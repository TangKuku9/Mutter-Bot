using QQBot.Core.Models;
using QQBot.Core.Models.Config;

namespace QQBot.Core.Interfaces;

public interface IContextBuilder
{
    Task<BuildContextResult> BuildGroupContextAsync(string groupId, string currentMessage, string senderName, CancellationToken ct = default);

    /// <summary>
    /// 换掉"上下文条数 / 输出上限"的群聊上下文构建。AI 择机发言用它，避免继承
    /// AI 面板里为 @ 回复调好的那套规模参数（两者需求不同）。
    /// </summary>
    Task<BuildContextResult> BuildGroupContextAsync(
        string groupId, string currentMessage, string senderName,
        Func<AiConfig, AiConfig>? overrideConfig, CancellationToken ct = default);

    Task<BuildContextResult> BuildPrivateContextAsync(string userId, string currentMessage, string senderName = "User", CancellationToken ct = default);
}
