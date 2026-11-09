namespace Mutter.Core.Configuration;

public class MutterOptions
{
    public string AppId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.bot.qq.com";

    /// <summary>富交互（markdown 卡片 / 按钮菜单 / @标签）开关与能力门控配置。
    /// 名称保留 "GameKeyboardMode" 是因为它已经是 appsettings 里的配置键值，改名会破坏现有部署。</summary>
    public RichUiOptions RichUi { get; set; } = new();

    /// <summary>运行时并发与超时预算（appsettings 的 Mutter:Runtime 节）。</summary>
    public RuntimeOptions Runtime { get; set; } = new();
}

/// <summary>
/// 并发调度与上游延迟韧性参数。生产实测上游延迟主导吞吐（P90 47s+，偶发挂死），
/// 这组旋钮把「多少并发、等多久、单次尝试烧多少预算、上游坏了怎么应对」交给部署按需调，而不是改代码。
/// 全部有钳制：运维写错值也不能把进程搞挂或把队列关死。
/// </summary>
public class RuntimeOptions
{
    /// <summary>会话工作队列的执行并发数。上游是慢速外部依赖时，槽位基本都在等响应，加并发是最直接的扩容手段。</summary>
    public int Parallelism { get; set; } = 3;

    /// <summary>全局队列容量（含正在执行的部分）。</summary>
    public int Capacity { get; set; } = 256;

    /// <summary>单会话排队上限（含正在执行的一项），防止一个群占满全局队列。</summary>
    public int PerConversationCap { get; set; } = 16;

    /// <summary>排队默认有效期：超过后按「等待回复超时」提示，不再占着队列。</summary>
    public int QueueAgeSeconds { get; set; } = 120;

    /// <summary>单个工作项的执行期限（含上下文构建与模型调用）。必须放得下根期限 + 投递窗口。</summary>
    public int WorkDeadlineSeconds { get; set; } = 240;

    /// <summary>主动评估（择机判定）的排队有效期。它可丢弃，排队比用户应答短得多。</summary>
    public int BackgroundQueueAgeSeconds { get; set; } = 45;

    /// <summary>整条生成流程（含降级链各阶段）共享的根期限。</summary>
    public int AiTimeoutSeconds { get; set; } = 120;

    /// <summary>单次模型尝试的上限。上游挂死时最多烧掉一个尝试份额，而不是把整条期限耗在一根悬空连接上。</summary>
    public int AiPerAttemptTimeoutSeconds { get; set; } = 90;

    /// <summary>上游降级期间前台排队的有效期：早点给一句诚实的提示，而不是让消息排到自然超时。</summary>
    public int DegradedQueueAgeSeconds { get; set; } = 45;

    /// <summary>上游降级期间单次尝试的收紧上限，让坏掉的尝试更快腾出槽位。</summary>
    public int DegradedPerAttemptTimeoutSeconds { get; set; } = 45;

    /// <summary>上游健康追踪的滚动窗口大小（最近 N 次模型调用）。</summary>
    public int HealthWindowSize { get; set; } = 10;

    /// <summary>窗口内失败达到该数量即判定上游降级。</summary>
    public int HealthFailureThreshold { get; set; } = 3;

    /// <summary>恢复条件：最近 5 次调用中成功达到该数量。</summary>
    public int HealthRecoveryThreshold { get; set; } = 4;

    /// <summary>降级最短持续时间（滞回），避免状态在好坏之间抖动。</summary>
    public int HealthMinDegradeSeconds { get; set; } = 60;

    /// <summary>联网改写是锦上添花：根期限剩余低于该值时直接发草稿，不让改写撞死在期限上。</summary>
    public int RewriteMinRemainingSeconds { get; set; } = 45;

    public RuntimeOptions Normalize()
    {
        Parallelism = Math.Clamp(Parallelism, 1, 16);
        Capacity = Math.Clamp(Capacity, 16, 2048);
        PerConversationCap = Math.Clamp(PerConversationCap, 1, 64);
        QueueAgeSeconds = Math.Clamp(QueueAgeSeconds, 15, 600);
        AiTimeoutSeconds = Math.Clamp(AiTimeoutSeconds, 30, 600);
        // 工作期限必须放得下根期限，否则还没等模型超时，队列先把工作取消了。
        WorkDeadlineSeconds = Math.Clamp(Math.Max(WorkDeadlineSeconds, AiTimeoutSeconds + 60), 60, 900);
        BackgroundQueueAgeSeconds = Math.Clamp(BackgroundQueueAgeSeconds, 10, 300);
        // 单次尝试超过根期限没有意义：预算重合正是「一次挂起烧光全部」的根因。
        AiPerAttemptTimeoutSeconds = Math.Clamp(Math.Min(AiPerAttemptTimeoutSeconds, AiTimeoutSeconds), 15, 600);
        DegradedQueueAgeSeconds = Math.Clamp(DegradedQueueAgeSeconds, 10, 600);
        DegradedPerAttemptTimeoutSeconds = Math.Clamp(DegradedPerAttemptTimeoutSeconds, 10, AiTimeoutSeconds);
        HealthWindowSize = Math.Clamp(HealthWindowSize, 5, 100);
        HealthFailureThreshold = Math.Clamp(HealthFailureThreshold, 2, HealthWindowSize);
        HealthRecoveryThreshold = Math.Clamp(HealthRecoveryThreshold, 1, 5);
        HealthMinDegradeSeconds = Math.Clamp(HealthMinDegradeSeconds, 0, 600);
        RewriteMinRemainingSeconds = Math.Clamp(RewriteMinRemainingSeconds, 10, 300);
        return this;
    }
}

/// <summary>消息按钮键盘模式。None=纯文字链或提示；Command=指令按钮(插入输入框)；Callback=回调按钮(需 INTERACTION intent)。</summary>
public enum GameKeyboardMode
{
    None = 0,
    Command = 1,
    Callback = 2
}

/// <summary>
/// 富交互配置。总开关 Enabled 默认关闭，逐群（GroupAllowList）灰度；
/// 各能力（Markdown / 键盘 / @标签）由 RichUiCapability 按平台实际返回错误码动态降级。
/// </summary>
public class RichUiOptions
{
    /// <summary>富交互总开关。关闭时所有面板回落到既有纯文本渲染（零行为变化）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>允许发送 msg_type=2 的 Markdown 消息。</summary>
    public bool Markdown { get; set; } = true;

    /// <summary>
    /// 按钮键盘模式：None / Command / Callback（Callback 需先获 INTERACTION 事件授权）。
    /// 故意用字符串承载：生产 appsettings 由运维手改，写错值不能导致进程启动即挂，
    /// 解析失败时按 None（纯文本菜单/文字链）兼容处理，见 <see cref="ResolveKeyboardMode"/>。
    /// </summary>
    public string KeyboardMode { get; set; } = "None";

    /// <summary>解析键盘模式；无法识别的值退回 None 而不抛异常。</summary>
    public GameKeyboardMode ResolveKeyboardMode() =>
        Enum.TryParse<GameKeyboardMode>(KeyboardMode, ignoreCase: true, out var mode) ? mode : GameKeyboardMode.None;

    /// <summary>无按钮组件权限时，用 markdown 内嵌文字链（&lt;qqbot-cmd-input&gt;）替代按钮。</summary>
    public bool CmdInputLinks { get; set; } = true;

    /// <summary>使用 &lt;qqbot-at-user&gt; 真实 @ 玩家（关闭则退回文本道号）。</summary>
    public bool AtUserTags { get; set; } = true;

    /// <summary>被动回复带 message_reference 引用原消息（默认关，观察兼容性后再开）。</summary>
    public bool MessageReference { get; set; } = false;

    /// <summary>订阅 INTERACTION_CREATE (1&lt;&lt;26) intent。⚠ 须先获平台授权，误配会导致网关断连。</summary>
    public bool InteractionIntent { get; set; } = false;

    /// <summary>灰度群白名单；空=全部生效。仅影响富交互，不影响纯文本兜底可用性。</summary>
    public List<string> GroupAllowList { get; set; } = new();

    /// <summary>某能力被平台拒绝后，重新探测的间隔分钟数（避免每条消息都撞一次坏权限）。</summary>
    public int CapabilityTtlMinutes { get; set; } = 360;

    /// <summary>键盘最大行数（官方上限 5）。</summary>
    public int MaxRows { get; set; } = 5;

    /// <summary>每行最大按钮数（官方上限 5）。</summary>
    public int MaxButtonsPerRow { get; set; } = 5;

    /// <summary>Markdown 正文最大字符数，超出截断（防 40054007 长度超限）。</summary>
    public int MaxMarkdownChars { get; set; } = 2000;
}
