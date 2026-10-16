using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mutter.Core.Interfaces;
using Mutter.Core.Models.Config;
using Mutter.Core.Proactive;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Entities;

namespace Mutter.Web.Controllers;

/// <summary>
/// 择机主动发言的统一管理入口：全局开关与参数、决策留痕、实时用量。
/// 所有写入都走 <see cref="IConfigRepository"/>，由模型把数值钳制在官方频控上限内。
/// </summary>
[ApiController]
[Route("api/proactive")]
public class ProactiveController : ControllerBase
{
    private static readonly string[] SentOutcomes = { ProactiveOutcomes.SentPassive, ProactiveOutcomes.SentActive };

    private readonly AppDbContext _db;
    private readonly IConfigRepository _configRepo;
    private readonly IActiveMessageService _activeMessage;
    private readonly ProactiveObserveService _observe;
    private readonly IQqGatewayConnection _gateway;
    private readonly IGroupMemoryRepository _memoryRepo;

    public ProactiveController(
        AppDbContext db,
        IConfigRepository configRepo,
        IActiveMessageService activeMessage,
        ProactiveObserveService observe,
        IQqGatewayConnection gateway,
        IGroupMemoryRepository memoryRepo)
    {
        _db = db;
        _configRepo = configRepo;
        _activeMessage = activeMessage;
        _observe = observe;
        _gateway = gateway;
        _memoryRepo = memoryRepo;
    }

    private static string? NormalizeGroupId(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>没存过摘要时的空壳：面板拿到它照样能渲染，不必处理 404。</summary>
    private static Mutter.Core.Models.GroupMemory EmptyMemory(string groupId) => new()
    {
        GroupId = groupId, Summary = string.Empty, UpdatedAt = DateTime.MinValue, Version = 0
    };

    public sealed class GroupMemoryDto
    {
        public string? Summary { get; set; }
    }

    [HttpGet("groups/{groupId}/memory")]
    public async Task<IActionResult> GetGroupMemory(string groupId, CancellationToken ct)
    {
        var id = NormalizeGroupId(groupId);
        if (id is null) return BadRequest(new { error = "缺少群 ID" });
        var memory = await _memoryRepo.GetAsync(id, ct);
        return Ok(memory ?? EmptyMemory(id));
    }

    [HttpPut("groups/{groupId}/memory")]
    public async Task<IActionResult> PutGroupMemory(string groupId, [FromBody] GroupMemoryDto dto, CancellationToken ct)
    {
        var id = NormalizeGroupId(groupId);
        if (id is null) return BadRequest(new { error = "缺少群 ID" });

        // 空摘要等于清空：面板把内容删干净就该回到"这群没有记忆"，
        // 而不是留一行 Summary 为空、但 UpdatedAt/Version 看着像模像样的空记忆。
        if (string.IsNullOrWhiteSpace(dto.Summary))
        {
            await _memoryRepo.DeleteAsync(id, ct);
            return Ok(EmptyMemory(id));
        }

        var memory = await _memoryRepo.SaveAsync(id, dto.Summary, ct);
        return Ok(memory);
    }

    [HttpDelete("groups/{groupId}/memory")]
    public async Task<IActionResult> DeleteGroupMemory(string groupId, CancellationToken ct)
    {
        var id = NormalizeGroupId(groupId);
        if (id is null) return BadRequest(new { error = "缺少群 ID" });
        await _memoryRepo.DeleteAsync(id, ct);
        return Ok(new { message = "群记忆已清空", GroupId = id });
    }

    public class ProactiveConfigDto
    {
        public bool Enabled { get; set; }
        public int WindowSeconds { get; set; } = 60;
        public int WindowMaxMessages { get; set; } = 12;
        public int CooldownMinutes { get; set; } = 45;
        public int MaxPerGroupPerDay { get; set; } = 3;
        public int MaxGlobalPerDay { get; set; } = 12;
        public int QuietStartHour { get; set; } = 23;
        public int QuietEndHour { get; set; } = 8;
        public int Liveliness { get; set; } = 1;
        public int MaxAttachedImages { get; set; } = ProactiveChatConfig.DefaultMaxAttachedImages;
        public int HistoryMaxMessages { get; set; } = ProactiveChatConfig.DefaultHistoryMaxMessages;
        public int EvalMaxTokens { get; set; } = ProactiveChatConfig.DefaultEvalMaxTokens;
        public bool AllowActiveChannel { get; set; } = true;
        public int ActivePerGroupPerMinute { get; set; } = 3;
        public int ActivePerGroupPerDay { get; set; } = 20;
        public int ActivePerAccountPerMinute { get; set; } = 20;
        public DateTime UpdatedAt { get; set; }

        /// <summary>官方频控上限，供面板把输入框的 max 对齐。</summary>
        public int CeilingGroupPerMinute { get; set; } = ProactiveChatConfig.CeilingGroupPerMinute;
        public int CeilingGroupPerDay { get; set; } = ProactiveChatConfig.CeilingGroupPerDay;
        public int CeilingAccountPerMinute { get; set; } = ProactiveChatConfig.CeilingAccountPerMinute;

        /// <summary>附图张数上限，供面板对齐输入框。</summary>
        public int CeilingAttachedImages { get; set; } = ProactiveChatConfig.CeilingAttachedImages;

        /// <summary>是否把群记忆注入择机判定（群级可覆盖）。</summary>
        public bool MemoryInjectionEnabled { get; set; } = true;
    }

    /// <summary>当前配置（含官方上限，供表单约束）。</summary>
    [HttpGet("config")]
    public async Task<ActionResult<ProactiveConfigDto>> GetConfig(CancellationToken ct)
    {
        var config = await _configRepo.GetProactiveConfigAsync(ct);
        return Ok(ToDto(config));
    }

    /// <summary>保存配置。返回值是钳制后的实际生效值，面板据此回填。</summary>
    [HttpPut("config")]
    public async Task<ActionResult<ProactiveConfigDto>> UpdateConfig([FromBody] ProactiveConfigDto dto, CancellationToken ct)
    {
        var config = new ProactiveChatConfig
        {
            Enabled = dto.Enabled,
            WindowSeconds = dto.WindowSeconds,
            WindowMaxMessages = dto.WindowMaxMessages,
            CooldownMinutes = dto.CooldownMinutes,
            MaxPerGroupPerDay = dto.MaxPerGroupPerDay,
            MaxGlobalPerDay = dto.MaxGlobalPerDay,
            QuietStartHour = dto.QuietStartHour,
            QuietEndHour = dto.QuietEndHour,
            Liveliness = dto.Liveliness,
            MaxAttachedImages = dto.MaxAttachedImages,
            HistoryMaxMessages = dto.HistoryMaxMessages,
            EvalMaxTokens = dto.EvalMaxTokens,
            AllowActiveChannel = dto.AllowActiveChannel,
            ActivePerGroupPerMinute = dto.ActivePerGroupPerMinute,
            ActivePerGroupPerDay = dto.ActivePerGroupPerDay,
            ActivePerAccountPerMinute = dto.ActivePerAccountPerMinute,
            MemoryInjectionEnabled = dto.MemoryInjectionEnabled
        };

        await _configRepo.SaveProactiveConfigAsync(config, ct);
        var saved = await _configRepo.GetProactiveConfigAsync(ct);

        return Ok(new { message = saved.Enabled ? "择机发言配置已保存（已开启）" : "择机发言配置已保存（当前为关闭）", Config = ToDto(saved) });
    }

    /// <summary>决策留痕分页。默认最近优先，可按群与结论过滤。</summary>
    [HttpGet("logs")]
    public async Task<ActionResult<object>> GetLogs(
        [FromQuery] string? groupId, [FromQuery] string? outcome,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var query = _db.ProactiveChatLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(groupId)) query = query.Where(l => l.GroupId == groupId);
        if (!string.IsNullOrWhiteSpace(outcome)) query = query.Where(l => l.Outcome == outcome);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(l => l.EvaluatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var names = await _db.KnownGroups.AsNoTracking()
            .ToDictionaryAsync(g => g.GroupOpenId, g => g.Name, ct);

        var items = rows.Select(l => new
        {
            l.Id,
            l.GroupId,
            GroupName = names.TryGetValue(l.GroupId, out var n) ? n : null,
            l.EvaluatedAt,
            l.MessageCount,
            l.Outcome,
            l.ShouldSpeak,
            l.ImageCount,
            l.MemoryCount,
            l.MemoryPromptChars,
            l.Topic,
            l.Line,
            l.Reason,
            l.Detail,
            l.Model,
            l.PromptTokens,
            l.CompletionTokens
        }).ToList();

        return Ok(new { items, totalCount = total, page, pageSize });
    }

    /// <summary>今日用量 + 待评估窗口 + 各群主动消息余量，供面板显示“现在到底有多吵”。</summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken ct)
    {
        var config = await _configRepo.GetProactiveConfigAsync(ct);
        // 日界按北京时间，落库时间存的是 UTC，故先取北京 00:00 再折回 UTC 时刻
        var todayUtcStart = DateTime.UtcNow.AddHours(8).Date.AddHours(-8);

        var sinceRows = await _db.ProactiveChatLogs.AsNoTracking()
            .Where(l => l.EvaluatedAt >= todayUtcStart)
            .GroupBy(l => l.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var participating = await _db.GroupConfigs.AsNoTracking()
            .Where(g => g.AiProactiveEnabled && g.IsEnabled)
            .Select(g => g.GroupId)
            .ToListAsync(ct);

        // 今天有多少次判定真的带了群记忆。留痕里已经有每次的记录，这里给一个一眼能看的数，
        // 否则"记忆到底有没有在工作"只能去翻日志或读代码。
        var memoryInjectionsToday = await _db.ProactiveChatLogs.AsNoTracking()
            .CountAsync(l => l.EvaluatedAt >= todayUtcStart && l.MemoryCount > 0, ct);

        var groupNames = await _db.KnownGroups.AsNoTracking()
            .ToDictionaryAsync(g => g.GroupOpenId, g => g.Name, ct);

        var quotas = new List<object>();
        foreach (var groupId in participating)
        {
            var quota = await _activeMessage.GetGroupQuotaStatusAsync(groupId, ct);
            quotas.Add(new
            {
                GroupId = groupId,
                Name = groupNames.TryGetValue(groupId, out var name) ? name : null,
                quota.UsedToday,
                quota.Quota,
                quota.SentLastMinute,
                quota.MinuteQuota,
                quota.Rejected,
                quota.LastSentAt
            });
        }

        var events = _gateway.EventCounts;
        events.TryGetValue("GROUP_MESSAGE_CREATE", out var fullMode);
        events.TryGetValue("GROUP_AT_MESSAGE_CREATE", out var atMode);

        var hint = fullMode > 0
            ? $"全量群消息已到达 {fullMode} 条，围观链路正常"
            : atMode > 0
                ? $"只收到 @ 事件（{atMode} 条），平台没有下发全量群消息：检查开放平台事件订阅是否勾选 GROUP_MESSAGE_CREATE，以及群设置里“可获取的群聊消息范围”是否为全部消息"
                : "本次启动后一条群消息都没收到（计数从进程启动开始）";

        return Ok(new
        {
            GatewayEvents = events,
            FullModeArrived = fullMode,
            AtModeArrived = atMode,
            DeliveryHint = hint,
            config.Enabled,
            config.AllowActiveChannel,
            config.MemoryInjectionEnabled,
            TodayMemoryInjections = memoryInjectionsToday,
            ParticipatingGroups = participating.Count,
            TodayByOutcome = sinceRows,
            TodaySent = sinceRows.Where(r => SentOutcomes.Contains(r.Outcome)).Sum(r => r.Count),
            TodayEvaluations = sinceRows.Sum(r => r.Count),
            Quotas = quotas,
            PendingWindows = _observe.PendingWindows()
                .Select(p => new { GroupId = p.GroupId, p.Pending })
        });
    }

    private static ProactiveConfigDto ToDto(ProactiveChatConfig c) => new()
    {
        Enabled = c.Enabled,
        WindowSeconds = c.WindowSeconds,
        WindowMaxMessages = c.WindowMaxMessages,
        CooldownMinutes = c.CooldownMinutes,
        MaxPerGroupPerDay = c.MaxPerGroupPerDay,
        MaxGlobalPerDay = c.MaxGlobalPerDay,
        QuietStartHour = c.QuietStartHour,
        QuietEndHour = c.QuietEndHour,
        Liveliness = c.Liveliness,
        MaxAttachedImages = c.MaxAttachedImages,
        HistoryMaxMessages = c.HistoryMaxMessages,
        EvalMaxTokens = c.EvalMaxTokens,
        AllowActiveChannel = c.AllowActiveChannel,
        ActivePerGroupPerMinute = c.ActivePerGroupPerMinute,
        ActivePerGroupPerDay = c.ActivePerGroupPerDay,
        ActivePerAccountPerMinute = c.ActivePerAccountPerMinute,
        MemoryInjectionEnabled = c.MemoryInjectionEnabled,
        UpdatedAt = c.UpdatedAt
    };
}
