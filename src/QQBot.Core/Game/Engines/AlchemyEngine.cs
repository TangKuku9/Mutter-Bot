using QQBot.Core.Game.Interfaces;
using QQBot.Core.Game.Models;

namespace QQBot.Core.Game.Engines;

/// <summary>
/// 炼丹引擎。校验材料 + 确定性 RNG 判定成丹品质（灵根属性影响对应五行丹药）。
/// 纯计算，不修改存档/背包（由服务层应用）。
/// </summary>
public class AlchemyEngine
{
    public AlchemyResult Brew(
        PlayerProfile player,
        List<PlayerInventoryItem> inventory,
        string? recipeKey,
        DateTime now)
    {
        var recipe = AlchemyRecipe.Find(recipeKey);
        if (recipe is null)
            return new AlchemyResult("unknown", null, null, 0, 0, 0,
                $"未知丹方。可炼：\n{string.Join("、", AlchemyRecipe.Recipes.Select(r => r.Name))}");

        // 校验材料
        foreach (var (matId, need) in recipe.Materials)
        {
            var have = inventory.FirstOrDefault(i => i.ItemId == matId)?.Quantity ?? 0;
            if (have < need)
                return new AlchemyResult("insufficient", recipe.ItemId, recipe.Name, 0, 0, 0,
                    $"材料不足：{AlchemyRecipe.MaterialName(matId)}×{need}\n持有仅 {have}。");
        }

        // 基础成功率 + 灵根五行契合加成
        var baseRate = recipe.BaseSuccess;
        var rootElement = SpiritRoot.NameOf(player.SpiritRootType) switch
        {
            "金灵根" => "金",
            "木灵根" => "木",
            "水灵根" => "水",
            "火灵根" => "火",
            "土灵根" => "土",
            _ => "变"
        };
        var affinity = rootElement == recipe.Element ? 0.10 : 0;
        // 境界加成：修为越高炼丹越稳（每境界 +2%），成功率封顶 90%
        var realmBonus = player.RealmIndex * GameConstants.AlchemyRealmBonusPerRealm;
        var chance = Math.Clamp(baseRate + affinity + realmBonus, 0.05, GameConstants.AlchemyMaxSuccessRate);

        // 种子化 RNG（混入背包状态：每次成/败都会消耗材料，
        // 同一小时窗口内重试掷点必然不同，避免"同小时重试必败/必成"的确定性结果）
        var seed = DeterministicSeed(player.MemberOpenId, recipe.ItemId, now, inventory);
        var rng = new Random(seed);
        var roll = rng.NextDouble();
        var success = roll < chance;

        if (!success)
            return new AlchemyResult("failure", recipe.ItemId, recipe.Name, 0, 0, chance,
                $"炼制【{recipe.Name}】失败\n材料尽毁，再接再厉。");

        // 成丹品质（确定性 RNG 续掷）
        var qRoll = rng.NextDouble();
        var quality = qRoll switch
        {
            < 0.1 => 4, // 完美
            < 0.35 => 3, // 优
            < 0.7 => 2,  // 良
            _ => 1       // 普通
        };

        return new AlchemyResult("success", recipe.ItemId, recipe.Name, quality,
            recipe.OutputCount, chance,
            $"炼制成功！\n获得 {QualityText(quality)}{recipe.Name} ×{recipe.OutputCount}。");
    }

    private static string QualityText(int q) => q switch
    {
        4 => "完美·",
        3 => "上品·",
        2 => "良品·",
        _ => ""
    };

    private static int DeterministicSeed(string memberOpenId, string itemId, DateTime now, List<PlayerInventoryItem> inventory)
    {
        var invFingerprint = string.Join(",", inventory.OrderBy(i => i.ItemId).Select(i => $"{i.ItemId}:{i.Quantity}"));
        var key = $"{memberOpenId}|{itemId}|{now:yyyy-MM-dd HH}|{invFingerprint}";
        var hash = 5381;
        foreach (var c in key)
            hash = ((hash << 5) + hash) ^ c;
        return hash & 0x7FFFFFFF;
    }
}

public record AlchemyResult(
    string Outcome,       // success/failure/insufficient/unknown
    string? ItemId,
    string? ItemName,
    int Quality,          // 1-4
    int OutputCount,
    double RolledChance,
    string Message);
