namespace QQBot.Core.Game.Models;

/// <summary>
/// 炼丹配方与材料目录。材料通过“探索”获取，丹药产出进入背包。
/// </summary>
public static class AlchemyRecipe
{
    public record Material(string Id, string Name);
    public record Recipe(string ItemId, string Name, string Element, double BaseSuccess,
        Dictionary<string, int> Materials, int OutputCount, string Desc);

    public static readonly Material[] Materials =
    {
        new("lingcao", "灵草"),
        new("yaocao", "药草"),
        new("lingkuang", "灵矿"),
        new("xuanbing_lu", "玄冰露"),
        new("huoyan_jing", "火焰晶"),
        new("zijing_zhi", "紫晶芝"),
    };

    public static readonly Recipe[] Recipes =
    {
        new("huiqi_dan", "回气丹", "木", 0.85,
            new() { ["lingcao"] = 3 }, 1, "恢复灵力，加速修炼"),
        new("zhuji_dan", "筑基丹", "土", 0.55,
            new() { ["lingcao"] = 5, ["yaocao"] = 2 }, 1, "稳固根基，提升突破成功率"),
        new("pojie_dan", "破界丹", "金", 0.35,
            new() { ["lingkuang"] = 3, ["xuanbing_lu"] = 1 }, 1, "破开瓶颈，大幅提升突破成功率"),
        new("huoyan_dan", "火焰丹", "火", 0.40,
            new() { ["huoyan_jing"] = 2, ["yaocao"] = 3 }, 1, "烈焰淬体，提升修行收益"),
        new("ningqi_dan", "凝气丹", "水", 0.70,
            new() { ["lingcao"] = 2, ["xuanbing_lu"] = 1 }, 1, "凝练灵力，温补修为"),
        new("zijing_dan", "紫晶丹", "土", 0.45,
            new() { ["zijing_zhi"] = 2, ["lingkuang"] = 2 }, 1, "紫晶芝炼就的珍贵丹药，大补灵力"),
        new("huichun_dan", "回春丹", "木", 0.55,
            new() { ["lingcao"] = 3, ["yaocao"] = 1 }, 1, "枯木逢春，服用可延寿元五载"),
    };

    public static Recipe? Find(string? itemIdOrName) =>
        Recipes.FirstOrDefault(r => r.ItemId == itemIdOrName || r.Name == itemIdOrName);

    public static string MaterialName(string id) =>
        Materials.FirstOrDefault(m => m.Id == id)?.Name ?? id;
}
