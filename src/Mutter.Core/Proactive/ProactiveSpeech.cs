using Mutter.Core.Models.Config;
using Mutter.Core.Services;

namespace Mutter.Core.Proactive;

/// <summary>
/// 择机发言第二阶段的触发条件。抽成纯函数是为了能被自检直接覆盖：
/// 联网改写只有当「择机开关 + 全局联网 + 真的有检索词 + 上游确实配了搜索通道」同时成立时才发生。
/// 少任何一条都直接发第一阶段的草稿，行为与联网功能上线前一致。
/// </summary>
public static class ProactiveSpeech
{
    public static bool ShouldSearch(
        ProactiveChatConfig proactive, AiConfig aiConfig, string? line, string? query)
        => proactive.WebSearchEnabled
           && aiConfig.WebSearchEnabled
           && AiChatService.HasConfiguredSearch(aiConfig)
           && !string.IsNullOrWhiteSpace(line)
           && !string.IsNullOrWhiteSpace(query);
}
