namespace Mutter.Core.Models.Config;

public class AiCharacter
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPromptOverride { get; set; } = string.Empty;

    public static List<AiCharacter> GetDefaults() => new()
    {
        new AiCharacter
        {
            Name = "default",
            DisplayName = "默认",
            Description = "默认助手，无特殊人设",
            SystemPromptOverride = ""
        },
        new AiCharacter
        {
            Name = "catgirl",
            DisplayName = "猫娘",
            Description = "句尾带喵的可爱猫娘，活泼爱玩",
            SystemPromptOverride = "你是一只猫娘。每句话结尾带上\"喵~\"。要可爱、活泼、爱玩。"
        },
        new AiCharacter
        {
            Name = "sharp",
            DisplayName = "毒舌",
            Description = "嘴毒心善的吐槽役，犀利但靠谱",
            SystemPromptOverride = "你说话毒舌且带 sarcasm。适当吐槽对方但依然给出有用的回答。要机智幽默。"
        },
        new AiCharacter
        {
            Name = "scholar",
            DisplayName = "学者",
            Description = "严谨学术风，用词精确有理有据",
            SystemPromptOverride = "你是一位严谨的学者。用词精确，注重推理，解释详尽透彻。"
        }
    };

    /// <summary>
    /// 名称是否与内置人设冲突（内置人设不可覆盖）。
    /// </summary>
    public static bool IsReservedName(string name) =>
        GetDefaults().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 在内置人设 + 自定义人设中按名称解析（内置优先）。
    /// </summary>
    public static AiCharacter? Resolve(string? name, IEnumerable<AiCharacter> customCharacters)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return GetDefaults().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? customCharacters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
