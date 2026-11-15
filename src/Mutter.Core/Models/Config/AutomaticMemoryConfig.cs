using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Mutter.Core.Services;

namespace Mutter.Core.Models.Config;

public sealed class AutomaticMemoryConfig
{
    public const int DefaultOutputTokens = 1024;
    public const int MinimumOutputTokens = 256;
    public const int MaximumOutputTokens = 8192;

    public bool Enabled { get; set; }
    public bool CountEnabled { get; set; } = true;
    public int Count { get; set; } = 50;
    public bool IntervalEnabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 120;
    public bool ScheduleEnabled { get; set; }
    public List<MemorySchedule> Schedules { get; set; } = [new()];
    public string ProviderConnectionId { get; set; } = "";
    public string ModelName { get; set; } = "";
    /// <summary>单次提炼的输出上限；包含思考模型的思考 token，并受模型元数据与上下文窗口再钳制。</summary>
    public int MaxOutputTokens { get; set; } = DefaultOutputTokens;

    /// <summary>实际请求的输出上限：用户设置、模型元数据上限和上下文窗口三者取最小。</summary>
    public int EffectiveOutputTokens(AiConnectionModel? model, int contextWindow)
    {
        var modelLimit = model?.MaxOutputTokens is > 0 ? model.MaxOutputTokens.Value : MaxOutputTokens;
        return AiRequestBudget.ClampOutputTokens(Math.Min(MaxOutputTokens, modelLimit), contextWindow);
    }

    public AutomaticMemoryConfig Normalize()
    {
        Count = Math.Clamp(Count, 1, 1000);
        IntervalMinutes = Math.Clamp(IntervalMinutes, 1, 10080);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, MinimumOutputTokens, MaximumOutputTokens);
        ProviderConnectionId = (ProviderConnectionId ?? "").Trim();
        ModelName = (ModelName ?? "").Trim();
        Schedules = (Schedules ?? []).Take(4).Where(s => s is not null).ToList();
        foreach (var schedule in Schedules) schedule.Normalize();
        return this;
    }

    public string? Error() => Enabled && !CountEnabled && !IntervalEnabled && !ScheduleEnabled
        ? "请至少启用一种学习条件"
        : Enabled && (ProviderConnectionId.Length == 0 || ModelName.Length == 0)
            ? "请选择独立的记忆总结服务商和模型"
            : ProviderConnectionId.Length > 256 || ModelName.Length > 256
                || ProviderConnectionId.Any(char.IsControl) || ModelName.Any(char.IsControl)
                ? "服务商或模型 ID 无效"
                : ScheduleEnabled && (Schedules.Count == 0 || Schedules.Any(s => !s.IsValid))
                    ? "定时任务需要有效的频率、日期和时刻" : null;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    public static AutomaticMemoryConfig Decode(string? json)
    {
        try { return (JsonSerializer.Deserialize<AutomaticMemoryConfig>(json ?? "{}", JsonOptions) ?? new()).Normalize(); }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return new(); }
    }
    public string Encode() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed class MemorySchedule
{
    public string Frequency { get; set; } = "daily";
    public string Time { get; set; } = "22:00";
    public int Weekday { get; set; } = 1;
    public bool IsValid => Frequency is "daily" or "workdays" or "weekly"
        && TimeOnly.TryParseExact(Time, "HH:mm", out _) && Weekday is >= 0 and <= 6;
    public void Normalize() { Frequency = (Frequency ?? "").Trim(); Time = (Time ?? "").Trim(); }
    public DateTime? NextAfter(DateTime utc)
    {
        if (!IsValid) return null;
        var time = TimeOnly.ParseExact(Time, "HH:mm");
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(8);
        for (var day = 0; day <= 8; day++)
        {
            var date = local.Date.AddDays(day);
            var weekday = (int)date.DayOfWeek;
            if (Frequency == "workdays" && weekday is 0 or 6 || Frequency == "weekly" && weekday != Weekday) continue;
            var candidate = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()).AddHours(-8), DateTimeKind.Utc);
            if (candidate > utc) return candidate;
        }
        return null;
    }
}
