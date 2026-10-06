namespace QQBot.Core.Game.Models;

/// <summary>
/// 修仙RPG 数值常量表。集中管理便于调参与回溯。
/// </summary>
public static class GameConstants
{
    // 挂机惰性结算
    public const int MaxIdleHours = 8;              // 离线收益封顶8小时
    public const double IdleDecayAfterHours = 4;    // 4小时后效率减半
    public const double BaseIdleRatePerHour = 10.0; // 基础修为/小时

    // 冷却时长
    public const int BreakthroughCooldownHours = 8;   // 原12h→8h，加速突破节奏
    public const int AlchemyCooldownMinutes = 30;
    public const int DailyCheckinCooldownHours = 20;
    public const int RefereeCooldownMinutes = 5;
    public const int ExploreCooldownHours = 1;

    // AI 事件
    public const int AiEventMinIntervalHours = 12;

    // AI 裁判：低修为玩家的保底基数（固定小额，防止突破清零后按境界阈值雪球刷修为）
    public const long RefereeMinEffectiveBase = 200;

    // 突破惩罚/奖励
    public const double BreakthroughFailureLossRatio = 0.20; // 失败损失20%修为（原30%）
    public const double BreakthroughSuccessBonusStones = 50;  // 突破成功奖励灵石

    // 签到
    public const long CheckinBaseStones = 500;  // 原20→500，10倍奖励
    public const long CheckinBaseCultivation = 30;
    public const int CheckinMaxStreakBonus = 7;

    // 修行（一次性大量修为）
    public const long PracticeBaseCultivation = 300;  // 原200→300，加速修为积累
    public const int PracticeCooldownHours = 8;

    // 炼丹成功率：境界加成与封顶（高境界炼丹更稳，但不超过 90%）
    public const double AlchemyRealmBonusPerRealm = 0.02; // 每境界成功率 +2%
    public const double AlchemyMaxSuccessRate = 0.90;     // 炼丹成功率上限

    // 探索：境界收益递增（材料数量与灵石随境界增长）
    public const double ExploreRealmMaterialBonus = 0.25; // 每境界材料数量 +25%（向上取整）
    public const long ExploreStonesBase = 30;             // 奇遇灵石基数，实际 ×(境界序号+1)

    // 灵石商店（P2.2 灵石消费出口）
    public const double ShopSellPriceRatio = 0.75; // 材料回收价 = 购入价 × 75%，向下取整
    public const long ShopPriceLingcao = 50;       // 灵草
    public const long ShopPriceYaocao = 80;        // 药草
    public const long ShopPriceLingkuang = 100;    // 灵矿
    public const long ShopPriceXuanbing = 150;     // 玄冰露
    public const long ShopPriceHuoyan = 150;       // 火焰晶
    public const long BtCooldownResetPrice = 500;  // 灵石重置突破冷却
}
