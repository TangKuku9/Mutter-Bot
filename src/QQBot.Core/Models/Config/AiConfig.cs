namespace QQBot.Core.Models.Config;

public class AiConfig
{
    /// <summary>
    /// 默认系统提示词，用户未填写 SystemPrompt 时使用
    /// </summary>
    public const string DefaultSystemPrompt =
        """
        你是 QQ 上的一位群友/好友，大家在群里 @ 你或私聊找你时你会回消息。

        聊天规则：
        1. 历史消息以「[昵称]: 内容」格式标注发言者，仅供你区分是谁在说话；你自己回复时直接说内容，不要模仿这个格式、不要带昵称前缀。
        2. 像真人网友一样聊天：口语化、简短自然，几个字能说清就别写一段话；别用"您"，别一本正经列 1234，除非对方真的在问需要展开的问题。
        3. 永远不要说"我是机器人/AI/人工智能/语言模型"之类的话，也不要说"作为AI我不能……"；就把自己当成一个普通网友来聊。
        4. QQ 不渲染 Markdown，禁止使用 #、*、```、表格等符号排版，纯文本说话。
        5. 群里多人聊天时注意分清谁在说什么，别张冠李戴；没 @ 你的历史消息只是背景，回复时针对当前找你的人。
        6. 不知道的事就说不知道或者不确定，别硬编；遇到违法、涉政敏感或人身攻击的话题，像正常人一样委婉带过或转移话题。
        """;

    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
    public int MaxContextMessages { get; set; } = 20;

    /// <summary>
    /// 是否启用联网搜索。开启后根据 API 地址自动推断服务商联网模式
    ///（通义千问 enable_search / 智谱 web_search tool）。
    /// </summary>
    public bool WebSearchEnabled { get; set; } = false;

    /// <summary>
    /// 组装最终送给 AI 的系统提示词：SystemPrompt（空则用默认值）+ 性格特点
    /// </summary>
    public string BuildEffectiveSystemPrompt()
    {
        var prompt = string.IsNullOrWhiteSpace(SystemPrompt) ? DefaultSystemPrompt : SystemPrompt;

        if (!string.IsNullOrWhiteSpace(Personality))
        {
            prompt += $"\n\n你的性格特点：\n{Personality}\n请在所有回复中始终保持这种性格。";
        }

        return prompt;
    }
}
