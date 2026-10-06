using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core;
using QQBot.Core.Configuration;
using QQBot.Core.Game.Services;
using QQBot.Core.Game.Ui;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;
using QQBot.Core.Models.QQOfficial;
using QQBot.Core.Proactive;
using QQBot.Core.Services;

namespace QQBot.Core.EventHandlers;

public class MessageEventHandler
{
    private readonly IMessageParser _parser;
    private readonly Services.MessageRouter _router;
    private readonly Services.BotCommandService _commandService;
    private readonly IContextBuilder _contextBuilder;
    private readonly IAiChatService _aiService;
    private readonly IQqBotApiService _apiService;
    private readonly IActiveMessageService _activeMessage;
    private readonly INotificationService _notificationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChannelWriter<ChatMessage> _messageWriter;
    private readonly GameCommandRouter _gameCommandRouter;
    private readonly CultivationGameService _cultivationGameService;
    private readonly RichUiCapability _richUi;
    private readonly IGameRecallService _gameRecall;
    private readonly ProactiveObserveService _observe;
    private readonly ReplySeqTracker _seqTracker;
    private readonly GroupMessageGate _groupGate;
    private readonly ILogger<MessageEventHandler> _logger;
    private string _botId = string.Empty;

    private readonly SemaphoreSlim _aiSemaphore = new(3, 3);

    private readonly ConcurrentDictionary<string, DateTime> _processedMsgIds = new();

    /// <summary>
    /// 内容级去重：防止 QQ 服务端用不同 msgId 重复投递同一条消息。
    /// Key = groupId:senderId:contentHash，Value = 首次处理时间。
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _recentContentKeys = new();
    private const int ContentDedupWindowSeconds = 30;

    /// <summary>
    /// 机器人回复去重：防止短时间内对同一群/用户重复发送相同内容的回复。
    /// 用于应对 QQ 服务端用不同 msgId/不同 @ 段格式重复投递同一条用户消息，
    /// 导致前级去重失败时，作为最后一道防线避免用户看到重复回复。
    /// Key = scope:contentHash，Value = 首次发送时间。
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _recentBotReplies = new();
    private const int BotReplyDedupWindowSeconds = 8;

    /// <summary>游标：已回应过的互动事件 ID（官方同一 interaction_id 只能回应一次）。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _handledInteractions = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public MessageEventHandler(
        IMessageParser parser,
        Services.MessageRouter router,
        Services.BotCommandService commandService,
        IContextBuilder contextBuilder,
        IAiChatService aiService,
        IQqBotApiService apiService,
        IActiveMessageService activeMessage,
        INotificationService notificationService,
        IServiceScopeFactory scopeFactory,
        ChannelWriter<ChatMessage> messageWriter,
        GameCommandRouter gameCommandRouter,
        CultivationGameService cultivationGameService,
        RichUiCapability richUi,
        IGameRecallService gameRecall,
        ProactiveObserveService observe,
        ReplySeqTracker seqTracker,
        GroupMessageGate groupGate,
        ILogger<MessageEventHandler> logger)
    {
        _parser = parser;
        _router = router;
        _commandService = commandService;
        _contextBuilder = contextBuilder;
        _aiService = aiService;
        _apiService = apiService;
        _activeMessage = activeMessage;
        _notificationService = notificationService;
        _scopeFactory = scopeFactory;
        _messageWriter = messageWriter;
        _gameCommandRouter = gameCommandRouter;
        _cultivationGameService = cultivationGameService;
        _richUi = richUi;
        _gameRecall = gameRecall;
        _observe = observe;
        _seqTracker = seqTracker;
        _groupGate = groupGate;
        _logger = logger;
    }

    public void SetBotId(string id) => _botId = id;

    private bool MarkProcessed(string msgId)
    {
        if (!_processedMsgIds.TryAdd(msgId, DateTime.UtcNow)) return false;

        if (_processedMsgIds.Count > 5000)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var kv in _processedMsgIds)
            {
                if (kv.Value < cutoff)
                    _processedMsgIds.TryRemove(kv.Key, out _);
            }
        }

        return true;
    }

    /// <summary>
    /// 内容级去重检查：同群+同用户+同内容在窗口期内视为重复。
    /// 指纹使用规范化文本（剥离所有 @ 段、合并空白），避免 QQ 服务端
    /// 用不同 @ 段格式（如 &lt;@!123&gt; vs &lt;@123&gt;）重复投递时绕过去重。
    /// </summary>
    private bool IsContentDuplicate(string groupId, string senderId, string content)
    {
        var contentFingerprint = NormalizeForFingerprint(content);
        // 截取前 100 字符作为指纹，避免长文本占用过多内存
        if (contentFingerprint.Length > 100)
            contentFingerprint = contentFingerprint[..100];
        var key = $"{groupId}:{senderId}:{contentFingerprint}";
        var now = DateTime.UtcNow;

        // 清理过期条目（低频执行）
        if (_recentContentKeys.Count > 500)
        {
            var cutoff = now.AddSeconds(-ContentDedupWindowSeconds);
            foreach (var kv in _recentContentKeys)
            {
                if (kv.Value < cutoff)
                    _recentContentKeys.TryRemove(kv.Key, out _);
            }
        }

        if (!_recentContentKeys.TryAdd(key, now))
        {
            // Key 已存在，检查是否在窗口期内
            if (_recentContentKeys.TryGetValue(key, out var firstSeen))
            {
                if ((now - firstSeen).TotalSeconds < ContentDedupWindowSeconds)
                    return true; // 重复

                // 窗口期已过，更新时间
                _recentContentKeys[key] = now;
            }
        }

        return false;
    }

    /// <summary>
    /// 指纹输入。纯图片/表情消息的文本是同一个占位，不带图片身份的话，
    /// 同一个人 30 秒内转发的两张不同图会被判成重复投递丢掉。
    /// </summary>
    private static string ContentSignature(ParsedMessage parsed)
    {
        // 指纹只在内存里去重，进程内自洽即可，不需要跨进程稳定的摘要
        return parsed.ImageUrls.Count == 0
            ? parsed.PlainText
            : $"{parsed.PlainText}#{parsed.ImageUrls[0].GetHashCode():X8}";
    }

    /// <summary>
    /// 规范化消息文本用于指纹比对：剥离所有 &lt;...&gt; 段（@ 提及、表情等），
    /// 合并多余空白。这样 QQ 服务端用不同 @ 段格式重复投递同一消息时，
    /// 指纹保持一致，能被 <see cref="IsContentDuplicate"/> 识别。
    /// </summary>
    private static string NormalizeForFingerprint(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var sb = new StringBuilder(content.Length);
        var inSegment = false;
        foreach (var c in content)
        {
            if (c == '<') { inSegment = true; continue; }
            if (inSegment)
            {
                if (c == '>') inSegment = false;
                continue;
            }
            sb.Append(c);
        }

        // 合并连续空白并 Trim，避免首尾空格差异导致指纹不一致
        var text = sb.ToString().Trim();
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var normalized = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) normalized.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                normalized.Append(c);
                lastWasSpace = false;
            }
        }
        return normalized.ToString();
    }

    /// <summary>
    /// 机器人回复去重检查：同群/同用户在窗口期内已发送过相同内容则跳过。
    /// 这是最后一道防线，防止前级用户消息去重失败时用户看到重复回复。
    /// </summary>
    private bool ShouldSkipDuplicateReply(string scope, string content)
    {
        var contentFingerprint = content.Length > 100 ? content[..100] : content;
        var key = $"{scope}:{contentFingerprint}";
        var now = DateTime.UtcNow;

        // 清理过期条目（低频执行）
        if (_recentBotReplies.Count > 200)
        {
            var cutoff = now.AddSeconds(-BotReplyDedupWindowSeconds);
            foreach (var kv in _recentBotReplies)
            {
                if (kv.Value < cutoff)
                    _recentBotReplies.TryRemove(kv.Key, out _);
            }
        }

        if (!_recentBotReplies.TryAdd(key, now))
        {
            if (_recentBotReplies.TryGetValue(key, out var firstSent))
            {
                if ((now - firstSent).TotalSeconds < BotReplyDedupWindowSeconds)
                    return true; // 重复，跳过

                _recentBotReplies[key] = now;
            }
        }

        return false;
    }

    public async Task HandleDispatchAsync(QqDispatch dispatch)
    {
        switch (dispatch.EventType)
        {
            case "GROUP_AT_MESSAGE_CREATE":
            case "GROUP_MESSAGE_CREATE":
            {
                var evt = dispatch.Data.Deserialize<GroupMessageEvent>(JsonOptions);
                if (evt is null) return;
                await HandleGroupMessageAsync(evt, isAtBot: dispatch.EventType == "GROUP_AT_MESSAGE_CREATE");
                break;
            }
            case "C2C_MESSAGE_CREATE":
            {
                var evt = dispatch.Data.Deserialize<C2cMessageEvent>(JsonOptions);
                if (evt is null) return;
                await HandleC2cMessageAsync(evt);
                break;
            }
            case "GROUP_ADD_ROBOT":
            {
                var e = dispatch.Data.Deserialize<GroupRobotEvent>(JsonOptions);
                if (e is null) return;
                using var scope = _scopeFactory.CreateScope();
                var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                await configRepo.AddKnownGroupAsync(e.GroupOpenid, null);
                // 自动创建默认群配置（启用），入群即可使用
                var existingConfig = await configRepo.GetGroupConfigAsync(e.GroupOpenid);
                if (existingConfig == null)
                {
                    await configRepo.SaveGroupConfigAsync(new GroupConfig
                    {
                        GroupId = e.GroupOpenid,
                        IsEnabled = true,
                        TriggerMode = 0
                    });
                    _logger.LogInformation("已为群 {GroupOpenid} 自动创建默认配置", e.GroupOpenid);
                }
                _logger.LogInformation("机器人被加入群组 GroupOpenid={GroupOpenid}", e.GroupOpenid);
                break;
            }
            case "GROUP_DEL_ROBOT":
            {
                var e = dispatch.Data.Deserialize<GroupRobotEvent>(JsonOptions);
                if (e is null) return;
                using var scope = _scopeFactory.CreateScope();
                var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                await configRepo.RemoveKnownGroupAsync(e.GroupOpenid);
                _logger.LogInformation("机器人被移出群组 GroupOpenid={GroupOpenid}", e.GroupOpenid);
                break;
            }
            case "FRIEND_ADD":
            {
                var e = dispatch.Data.Deserialize<FriendEvent>(JsonOptions);
                if (e is null) return;
                using var scope = _scopeFactory.CreateScope();
                var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                await configRepo.AddKnownUserAsync(e.Openid, null);
                // 自动启用私聊
                await configRepo.SetPrivateChatEnabledAsync(e.Openid, true);
                _logger.LogInformation("新增好友 UserOpenid={Openid}，已自动启用私聊", e.Openid);
                break;
            }
            case "FRIEND_DEL":
                _logger.LogInformation("用户删除好友");
                break;
            case "C2C_MSG_REJECT":
            {
                var e = dispatch.Data.Deserialize<MsgOptEvent>(JsonOptions);
                if (e?.UserOpenid is not null)
                    await _activeMessage.MarkUserRejectedAsync(e.UserOpenid);
                break;
            }
            case "GROUP_MSG_REJECT":
            {
                var e = dispatch.Data.Deserialize<MsgOptEvent>(JsonOptions);
                if (e?.GroupOpenid is not null)
                    await _activeMessage.MarkGroupRejectedAsync(e.GroupOpenid);
                break;
            }
            case "GROUP_MSG_RECEIVE":
            {
                var e = dispatch.Data.Deserialize<MsgOptEvent>(JsonOptions);
                if (e?.GroupOpenid is not null)
                    await _activeMessage.ClearGroupRejectedAsync(e.GroupOpenid);
                _logger.LogInformation("群 {GroupOpenid} 重新允许接收主动消息", e?.GroupOpenid);
                break;
            }
            case "C2C_MSG_RECEIVE":
            {
                var e = dispatch.Data.Deserialize<MsgOptEvent>(JsonOptions);
                if (e?.UserOpenid is not null)
                    await _activeMessage.ClearUserRejectedAsync(e.UserOpenid);
                _logger.LogInformation("用户 {UserOpenid} 重新允许接收主动消息", e?.UserOpenid);
                break;
            }
            case "INTERACTION_CREATE":
            {
                var evt = dispatch.Data.Deserialize<InteractionCreateEvent>(JsonOptions);
                if (evt is null || string.IsNullOrEmpty(evt.Id)) return;
                if (evt.NeedsResponse) _richUi.ReportInteractionAvailable();   // 回调链路已实通，允许下发回调按钮
                if (!evt.NeedsResponse)
                {
                    // 13 赞踩 / 14 清空会话 / 18~ 授权等无需回应，先留痕便于后续做反馈统计
                    _logger.LogDebug("互动事件（无需回应）type={Type} scene={Scene}", evt.Type, evt.Scene);
                    return;
                }
                await HandleButtonInteractionAsync(evt);
                break;
            }
            case "READY":
                _logger.LogInformation("机器人已就绪 Ready");
                break;
            default:
                _logger.LogDebug("未处理事件 {EventType}", dispatch.EventType);
                break;
        }
    }

    private async Task HandleGroupMessageAsync(GroupMessageEvent evt, bool isAtBot)
    {
        if (evt.Author?.Bot == true) return;
        if (string.IsNullOrEmpty(evt.Id)) return;
        if (!_groupGate.ShouldHandle(evt.Id, isAtBot, out var reprocessAsAt)) return;

        if (reprocessAsAt)
            _logger.LogInformation("全量事件先到达，@ 事件补走回复链路: MsgId={MessageId}", evt.Id);

        var parsed = _parser.Parse(evt.Content, evt.Attachments);

        // 先路由：全量模式下平台不再单独下发 @ 事件，"有没有被 @"只能靠群配置的识别码从
        // content 里认出来。认出来就按 @ 处理（必须回话），并且像官方那样把 at 段剥掉再喂模型。
        var route = await _router.RouteGroupAsync(evt.GroupOpenid, isAtBot, evt.Content);
        var mention = isAtBot || route.BotMentionCode is not null;
        if (route.BotMentionCode is not null)
        {
            _logger.LogInformation("按识别码判定为 @ 机器人（本次未收到 @ 事件）: GroupId={GroupId}, MsgId={MessageId}",
                evt.GroupOpenid, evt.Id);
            parsed.PlainText = BotMention.Strip(parsed.PlainText, route.BotMentionCode);
        }
        parsed.HasAtBot = mention;

        // 内容级去重：QQ 服务端可能用不同 msgId 重复投递同一消息。
        // 但游戏指令（如「突破」「修炼」）跳过此检查：去重窗口(30s)会误杀
        // 渡劫结算等需要短时间内重复发送相同指令的玩法（2026-08-07 事故根因）。
        // 消息 ID 级去重（GroupMessageGate）仍在，足以拦截 QQ 同 msgId 重复投递。
        var senderId = evt.Author?.MemberOpenid ?? string.Empty;
        // 优先用平台下发的真实昵称（历史上只用了 OpenId，面板只能显示“道友XXXX”）
        var senderName = !string.IsNullOrWhiteSpace(evt.Author?.Username)
            ? evt.Author!.Username!
            : senderId.Length > 0 ? senderId : "Member";
        GameCommand? gameCmd = mention ? _gameCommandRouter.TryMatch(parsed.PlainText) : null;
        if (!reprocessAsAt && gameCmd is null && IsContentDuplicate(evt.GroupOpenid, senderId, ContentSignature(parsed)))
        {
            _logger.LogWarning("检测到重复投递，跳过: GroupId={GroupId}, Sender={SenderId}, Content={Content}",
                evt.GroupOpenid, senderId, parsed.PlainText.Length > 50 ? parsed.PlainText[..50] + "..." : parsed.PlainText);
            return;
        }

        if (!route.ShouldProcess && !route.ShouldObserve) return;

        var role = evt.Author?.MemberRole;

        var chatMsg = new ChatMessage
        {
            MessageId = evt.Id,
            ChatType = 0,
            GroupId = evt.GroupOpenid,
            UserId = senderId,
            UserName = senderName,
            PlainText = parsed.PlainText,
            RawSegmentsJson = parsed.RawSegmentsJson,
            HasImage = parsed.HasImage,
            HasAtBot = parsed.HasAtBot,
            IsBotMessage = false,
            CreatedAt = DateTime.UtcNow
        };

        // 只有围观档（TriggerMode=2）或 @ 到才落库；纯择机观察的消息留在内存窗口，评估完即丢，
        // 不进历史库也不推给面板 —— 群友原话要不要留，是每群的设置而不是全局开关。
        // reprocessAsAt 时这条已由全量事件落过库，只把“被 @ 的是 bot”补上，避免重复入库与重复弹卡片。
        if (reprocessAsAt)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            try { await repo.MarkAtBotAsync(evt.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "补记 @ 标记失败 MsgId={MessageId}", evt.Id); }
        }
        else if (route.ShouldProcess)
        {
            await _messageWriter.WriteAsync(chatMsg);

            try
            {
                await _notificationService.NotifyNewMessageAsync(new
                {
                    chatMsg.Id, chatMsg.ChatType, chatMsg.GroupId, chatMsg.UserId,
                    chatMsg.UserName, chatMsg.PlainText, chatMsg.HasImage,
                    chatMsg.HasAtBot, chatMsg.IsBotMessage, chatMsg.CreatedAt
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "Notify new message failed"); }
        }

        _logger.LogDebug("Message queued: MsgId={MessageId}, Type={MessageType}, User={UserName}",
            evt.Id, "group", senderName);

        if (Services.BotCommandService.LooksLikeCommand(parsed.PlainText) && role is "owner" or "admin")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // /ui：用当前消息的被动回复槽位实探 markdown / 按钮能力（零主动配额）
                    if (parsed.PlainText.TrimStart().StartsWith("/ui", StringComparison.OrdinalIgnoreCase))
                    {
                        var report = await ProbeRichUiAsync(evt.GroupOpenid, evt.Id);
                        await SendReplyAsync(evt.GroupOpenid, null, evt.Id, report);
                        return;
                    }

                    var reply = await _commandService.ExecuteGroupCommandAsync(senderId, parsed.PlainText, evt.GroupOpenid, role);
                    await SendReplyAsync(evt.GroupOpenid, null, evt.Id, reply);
                }
                catch (Exception ex) { _logger.LogError(ex, "Group command failed"); }
            });
            return;
        }

        if (mention)
        {
            // 游戏指令路由：命中修仙RPG 关键词则走游戏，未命中继续走 AI 回复
            // （gameCmd 已在去重检查前预匹配，确保游戏指令绕过内容级去重）
            if (gameCmd != null)
            {
                // 自由行动：显式前缀触发 AI 裁判
                if (gameCmd.Action == GameAction.FreeAction)
                {
                    if (string.IsNullOrWhiteSpace(gameCmd.Args))
                    {
                        await SendReplyAsync(evt.GroupOpenid, null, evt.Id,
                            "请描述你想做的事，如：@我 行动 去后山采药", recallGame: true);
                        return;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var gameReply = await _cultivationGameService.HandleFreeActionAsync(
                                evt.GroupOpenid, senderId, senderName, gameCmd.Args);
                            if (gameReply is null)
                            {
                                await SendReplyAsync(evt.GroupOpenid, null, evt.Id, "AI裁判暂不可用，请稍后再试。", recallGame: true);
                                return;
                            }
                            // 保存到聊天历史 + Dashboard 统计
                            await SaveBotMessageAsync(evt.GroupOpenid, gameReply.Reply,
                                gameReply.AiModelUsed, gameReply.PromptTokens, gameReply.CompletionTokens);
                            if (gameReply.AiModelUsed is not null)
                            {
                                try
                                {
                                    await _notificationService.NotifyAiResponseCompletedAsync(new
                                    {
                                        GroupId = evt.GroupOpenid, Model = gameReply.AiModelUsed,
                                        TotalTokens = (gameReply.PromptTokens ?? 0) + (gameReply.CompletionTokens ?? 0),
                                        Content = gameReply.Reply
                                    });
                                }
                                catch (Exception ex) { _logger.LogError(ex, "Notify AI complete failed"); }
                            }
                            await SendReplyAsync(evt.GroupOpenid, null, evt.Id, gameReply, evt.MessageScene?.MsgIdx);
                        }
                        catch (Exception ex) { _logger.LogError(ex, "Free action failed"); }
                    });
                    return;
                }

                // 确定性游戏指令
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await _cultivationGameService.HandleAsync(
                            evt.GroupOpenid, senderId, senderName, gameCmd);
                        await SaveBotMessageAsync(evt.GroupOpenid, reply.Reply);
                        // 被动回复，零配额；带面板时同一消息里挂上菜单
                        await SendReplyAsync(evt.GroupOpenid, null, evt.Id, reply, evt.MessageScene?.MsgIdx);
                    }
                    catch (Exception ex) { _logger.LogError(ex, "Game command failed"); }
                });
                return;
            }

            // 未命中游戏指令 -> 走原 AI 角色扮演
            _ = Task.Run(async () =>
            {
                try { await ProcessAiReplyAsync(evt.GroupOpenid, null, senderId, senderName, parsed, evt.Id); }
                catch (Exception ex) { _logger.LogError(ex, "AI reply failed"); }
            });
        }
        else if (route.ShouldObserve)
        {
            _observe.Observe(evt.GroupOpenid, senderName, parsed.PlainText, evt.Id, parsed.ImageUrls);
        }
    }

    private async Task HandleC2cMessageAsync(C2cMessageEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Id)) return;
        if (!MarkProcessed(evt.Id)) return;

        var parsed = _parser.Parse(evt.Content, evt.Attachments);

        var shouldProcess = await _router.ShouldProcessPrivateAsync(evt.Author?.UserOpenid ?? string.Empty);
        if (!shouldProcess) return;

        var senderId = evt.Author?.UserOpenid ?? string.Empty;
        var senderName = !string.IsNullOrWhiteSpace(evt.Author?.Username)
            ? evt.Author!.Username!
            : senderId.Length > 0 ? senderId : "User";

        var chatMsg = new ChatMessage
        {
            MessageId = evt.Id,
            ChatType = 1,
            GroupId = null,
            UserId = senderId,
            UserName = senderName,
            PlainText = parsed.PlainText,
            RawSegmentsJson = parsed.RawSegmentsJson,
            HasImage = parsed.HasImage,
            HasAtBot = parsed.HasAtBot,
            IsBotMessage = false,
            CreatedAt = DateTime.UtcNow
        };

        await _messageWriter.WriteAsync(chatMsg);

        try
        {
            await _notificationService.NotifyNewMessageAsync(new
            {
                chatMsg.Id, chatMsg.ChatType, chatMsg.GroupId, chatMsg.UserId,
                chatMsg.UserName, chatMsg.PlainText, chatMsg.HasImage,
                chatMsg.HasAtBot, chatMsg.IsBotMessage, chatMsg.CreatedAt
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Notify new message failed"); }

        _logger.LogDebug("Message queued: MsgId={MessageId}, Type={MessageType}, User={UserName}",
            evt.Id, "private", senderName);

        if (Services.BotCommandService.LooksLikeCommand(parsed.PlainText))
        {
            using var scope = _scopeFactory.CreateScope();
            var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            if (await configRepo.IsPrivateChatAdminAsync(senderId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await _commandService.ExecuteAsync(senderId, parsed.PlainText);
                        await SendReplyAsync(null, senderId, evt.Id, reply);
                    }
                    catch (Exception ex) { _logger.LogError(ex, "Background task failed"); }
                });
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try { await ProcessAiReplyAsync(null, senderId, senderId, senderName, parsed, evt.Id); }
            catch (Exception ex) { _logger.LogError(ex, "Background task failed"); }
        });
    }

    private async Task ProcessAiReplyAsync(string? groupOpenId, string? userOpenId, string senderId, string senderName, ParsedMessage parsed, string msgId)
    {
        // 排队窗口 15s：单次回复通常 3~8s，5s 会把突发消息直接拒掉；等待是异步的，不占线程。
        // 主动接话那条路径仍用 5s——它被拒时可静默跳过，不该长时间占位。
        if (!await _aiSemaphore.WaitAsync(TimeSpan.FromSeconds(15)))
        {
            await SendReplyAsync(groupOpenId, userOpenId, msgId, "AI 繁忙，请稍后再试。");
            return;
        }

        try
        {
            try { await _notificationService.NotifyAiResponseStartedAsync(groupOpenId, senderName); }
            catch (Exception ex) { _logger.LogError(ex, "Notify AI start failed"); }

            BuildContextResult context;
            if (groupOpenId is not null)
                context = await _contextBuilder.BuildGroupContextAsync(groupOpenId, parsed.PlainText, senderName);
            else
                context = await _contextBuilder.BuildPrivateContextAsync(senderId, parsed.PlainText, senderName);

            var systemPrompt = context.ComposeSystemPrompt();

            AiChatResponse response;

            if (parsed.ImageUrls.Count > 0)
            {
                var historyWithImages = new List<(string Role, string Content, string? ImageUrl)>();
                foreach (var msg in context.Messages)
                    historyWithImages.Add((msg.Role, msg.Content, null));
                if (historyWithImages.Count > 0)
                {
                    var last = historyWithImages[^1];
                    historyWithImages[^1] = (last.Role, last.Content, parsed.ImageUrls[0]);
                }
                response = await _aiService.ChatAsync(systemPrompt, historyWithImages, context.EffectiveConfig);
            }
            else
            {
                response = await _aiService.ChatAsync(systemPrompt, context.Messages, context.EffectiveConfig);
            }

            var botMsg = new ChatMessage
            {
                MessageId = $"bot-{Guid.NewGuid():N}",
                ChatType = groupOpenId is not null ? 0 : 1,
                GroupId = groupOpenId,
                UserId = _botId,
                // 私聊上下文靠这个字段认领"这条回复是对谁说的"；不写就等于下次读不到自己上一轮的话
                PeerUserId = groupOpenId is null ? userOpenId : null,
                UserName = "Bot",
                PlainText = response.Content,
                IsBotMessage = true,
                AiModelUsed = response.Model,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                CreatedAt = DateTime.UtcNow
            };

            // 直接持久化 Bot 消息（而非走 channel 异步落库），确保推送通知前已写入数据库，
            // 否则前端收到 AiResponseCompleted 后刷新会因竞态查不到该 Bot 消息。
            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
                await repo.SaveMessageAsync(botMsg);
            }

            try
            {
                await _notificationService.NotifyAiResponseCompletedAsync(new
                {
                    GroupId = groupOpenId, Model = response.Model,
                    TotalTokens = response.TotalTokens, Content = response.Content
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "Notify AI complete failed"); }

            await SendReplyAsync(groupOpenId, userOpenId, msgId, response.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI processing failed");
            await SendReplyAsync(groupOpenId, userOpenId, msgId, "AI 处理失败，请稍后再试。");
        }
        finally { _aiSemaphore.Release(); }
    }

    private async Task SendReplyAsync(string? groupOpenId, string? userOpenId, string? msgId, string content, bool recallGame = false)
    {
        // 回复去重：同群/同用户在窗口期内已发送过相同内容则跳过，
        // 防止 QQ 服务端用不同 msgId/不同 @ 段格式重复投递时用户看到重复回复。
        // 注意：仅在 msgId 不为空（即被动回复场景）时启用，避免影响主动推送。
        if (msgId is not null && ShouldSkipReply(groupOpenId, userOpenId, msgId, content)) return;

        try
        {
            if (groupOpenId is not null)
            {
                var sentId = await _apiService.SendGroupMessageAsync(groupOpenId, content, msgId, _seqTracker.Next(msgId));
                if (recallGame) _gameRecall.Schedule(groupOpenId, sentId);
            }
            else if (userOpenId is not null)
                await _apiService.SendPrivateMessageAsync(userOpenId, content, msgId, _seqTracker.Next(msgId));
        }
        catch (QqApiException ex) when (msgId is not null && (groupOpenId is not null || userOpenId is not null))
        {
            _logger.LogWarning("被动回复失败，降级主动消息 err={ErrCode}", ex.ErrCode);
            if (groupOpenId is not null)
            {
                var result = await _activeMessage.SendGroupActiveAsync(groupOpenId, content);
                if (recallGame) _gameRecall.Schedule(groupOpenId, result.MessageId);
            }
            else
                await _activeMessage.SendPrivateActiveAsync(userOpenId!, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send reply failed");
        }
    }

    private enum DegradeKind { None, Keyboard, Markdown }

    /// <summary>
    /// 富交互回复出口：一条消息内同时完成“面板正文 + 菜单”，不额外发第二条。
    /// 被平台拒绝时就地按能力降级：按钮被拒 → 无按钮 markdown；markdown 被拒 → 纯文本；
    /// 被动槽位失效 → 沿用原有“降级主动消息”分支。降级重发占用下一个 msg_seq，
    /// 而官方每消息被动回复上限 5 次，故仅在真被拒时多花一个槽位。
    /// </summary>
    private async Task SendReplyAsync(
        string? groupOpenId, string? userOpenId, string? msgId, GameUiReply reply,
        string? refMsgIdx = null, string? eventId = null)
    {
        var panel = reply.Panel;
        var useMarkdown = panel is not null && panel.Markdown.Length > 0
            && _richUi.UseMarkdown(groupOpenId);
        var refId = _richUi.UseMessageReference(groupOpenId) ? refMsgIdx : null;

        if (msgId is not null && ShouldSkipReply(groupOpenId, userOpenId, msgId,
                useMarkdown ? panel!.Markdown : reply.Reply))
            return;

        var outbound = new OutboundMessage
        {
            PlainText = reply.Reply,
            MarkdownText = useMarkdown ? panel!.Markdown : null,
            Keyboard = useMarkdown ? panel!.Keyboard : null,
            MsgId = msgId,
            EventId = eventId,
            MsgSeq = _seqTracker.Next(msgId),
            RefMessageId = refId,
        };

        var degrade = DegradeKind.None;
        try
        {
            var sentId = await SendOutboundAsync(groupOpenId, userOpenId, outbound);
            if (groupOpenId is not null) _gameRecall.Schedule(groupOpenId, sentId);
            if (useMarkdown)
            {
                _richUi.MarkOk(RichUiCapability.CapMarkdown);
                _richUi.CountMarkdownSent();
                if (outbound.Keyboard is not null) _richUi.MarkOk(RichUiCapability.CapKeyboard);
            }
            return;
        }
        catch (QqApiException ex) when (useMarkdown && outbound.Keyboard is not null
                                        && QqApiErrorCodes.IsKeyboardDenied(ex.ErrCode))
        {
            // 按钮组件无权限/参数被拒：后续消息会自动改为文字链或无按钮，本条先保证发出去
            _richUi.ReportDenied(RichUiCapability.CapKeyboard, ex.ErrCode);
            degrade = DegradeKind.Keyboard;
            _logger.LogWarning("RichUi 按钮被拒，本条降级无按钮 markdown err={ErrCode}", ex.ErrCode);
        }
        catch (QqApiException ex) when (useMarkdown && QqApiErrorCodes.IsMarkdownDenied(ex.ErrCode))
        {
            _richUi.ReportDenied(RichUiCapability.CapMarkdown, ex.ErrCode);
            degrade = DegradeKind.Markdown;
            _logger.LogWarning("RichUi markdown 被拒，降级纯文本 err={ErrCode}", ex.ErrCode);
        }
        catch (QqApiException ex) when ((msgId is not null || eventId is not null)
                                        && (groupOpenId is not null || userOpenId is not null))
        {
            _logger.LogWarning("被动回复失败，降级主动消息 err={ErrCode}", ex.ErrCode);
            _richUi.CountPlainFallback();
            if (groupOpenId is not null)
            {
                var result = await _activeMessage.SendGroupActiveAsync(groupOpenId, reply.Reply);
                _gameRecall.Schedule(groupOpenId, result.MessageId);
            }
            else if (userOpenId is not null)
                await _activeMessage.SendPrivateActiveAsync(userOpenId, reply.Reply);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send rich reply failed");
            return;
        }

        if (degrade == DegradeKind.None) return;
        _richUi.CountDegradeRetry();
        if (degrade == DegradeKind.Markdown) _richUi.CountPlainFallback();

        var retry = degrade == DegradeKind.Keyboard
            ? outbound with { Keyboard = null, MsgSeq = _seqTracker.Next(msgId) }
            : outbound with { MarkdownText = null, Keyboard = null, MsgSeq = _seqTracker.Next(msgId) };

        try
        {
            var retryId = await SendOutboundAsync(groupOpenId, userOpenId, retry);
            if (groupOpenId is not null) _gameRecall.Schedule(groupOpenId, retryId);
        }
        catch (QqApiException ex) when (groupOpenId is not null || userOpenId is not null)
        {
            _logger.LogWarning("降级重发仍失败，转主动消息 err={ErrCode}", ex.ErrCode);
            if (groupOpenId is not null)
            {
                var result = await _activeMessage.SendGroupActiveAsync(groupOpenId, reply.Reply);
                _gameRecall.Schedule(groupOpenId, result.MessageId);
            }
            else if (userOpenId is not null)
                await _activeMessage.SendPrivateActiveAsync(userOpenId!, reply.Reply);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Degraded reply failed");
        }
    }

    private async Task<string?> SendOutboundAsync(string? groupOpenId, string? userOpenId, OutboundMessage msg)
    {
        if (groupOpenId is not null)
            return await _apiService.SendGroupRichAsync(groupOpenId, msg);
        if (userOpenId is not null)
            return await _apiService.SendPrivateRichAsync(userOpenId, msg);
        return null;
    }

    /// <summary>回复内容去重（仅被动回复场景）。返回 true 表示应跳过发送。</summary>
    private bool ShouldSkipReply(string? groupOpenId, string? userOpenId, string? msgId, string content)
    {
        var scope = groupOpenId is not null ? $"g:{groupOpenId}" : $"u:{userOpenId}";
        if (!ShouldSkipDuplicateReply(scope, content)) return false;

        _logger.LogWarning("检测到重复回复，跳过发送: Scope={Scope}, MsgId={MsgId}, Content={Content}",
            scope, msgId, content.Length > 50 ? content[..50] + "..." : content);
        return true;
    }

    /// <summary>
    /// 按钮回调。官方要求 3 秒内回应互动，否则客户端一直 loading，因此“先 ACK、后异步结算”，
    /// 结算结果用 event_id 走被动回复（不占用主动消息的分钟/每日频控额度）。
    /// </summary>
    private async Task HandleButtonInteractionAsync(InteractionCreateEvent evt)
    {
        var now = DateTime.UtcNow;
        if (!_handledInteractions.TryAdd(evt.Id, now))
        {
            _logger.LogDebug("重复互动事件已忽略 interaction={Id}", evt.Id);
            return;
        }
        PruneHandledInteractions(now);

        // 无论后续能不能处理，先 ACK 解除用户侧 loading（最多等 1.5s，不拖死事件回包）
        await AckInteractionAsync(evt.Id);

        if (evt.ChatType != 1 || string.IsNullOrEmpty(evt.GroupOpenid) || string.IsNullOrEmpty(evt.ActorOpenid))
        {
            _logger.LogDebug("非群聊按钮事件，仅 ACK scene={Scene}", evt.Scene);
            return;
        }

        var cmd = _gameCommandRouter.TryMatchButtonCommand(evt.ButtonData);
        if (cmd is null)
        {
            _logger.LogWarning("按钮 data 未命中游戏指令 data={Data}", evt.ButtonData);
            return;
        }

        var groupId = evt.GroupOpenid!;
        var actorId = evt.ActorOpenid!;

        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await _cultivationGameService.HandleAsync(groupId, actorId, actorId, cmd);
                await SaveBotMessageAsync(groupId, reply.Reply);
                // 被动回复的 event_id 必须带 "INTERACTION_CREATE:" 前缀（裸 Id 会被判 40034025 无效）
                await SendReplyAsync(groupId, null, msgId: null, reply, eventId: evt.PassiveReplyEventId);
            }
            catch (Exception ex) { _logger.LogError(ex, "Button interaction failed group={GroupId}", groupId); }
        });
    }

    private async Task AckInteractionAsync(string interactionId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            await _apiService.ReplyInteractionAsync(interactionId, InteractionReply.Ok(), cts.Token);
        }
        catch (Exception ex)
        {
            // ACK 失败只影响用户侧按钮转圈超时，不影响已经发出的结算
            _logger.LogWarning(ex, "回应互动失败 interaction={Id}", interactionId);
        }
    }

    /// <summary>
    /// 富交互实探：向本群发两条探测消息（纯 markdown / markdown+按钮），
    /// 用平台真实返回结果确定能力态，帮管理员判断能否进入下一个灰度阶段。
    /// 走被动槽位，不花主动消息配额。
    /// </summary>
    private async Task<string> ProbeRichUiAsync(string groupOpenId, string msgId)
    {
        var markdown = await TryProbeAsync(groupOpenId, msgId, withKeyboard: false);
        var keyboard = await TryProbeAsync(groupOpenId, msgId, withKeyboard: true);
        _richUi.ResetForDiagnostics();   // 按最新探测结果重新决策，不等 TTL

        var snap = _richUi.Snapshot();
        var opt = _richUi.Options;
        return "富交互自检\n"
            + $"markdown：{markdown}\n"
            + $"按钮：{keyboard}\n"
            + $"配置：总开关={(opt.Enabled ? "开" : "关")} 键盘={opt.KeyboardMode} "
            + $"文字链={(opt.CmdInputLinks ? "开" : "关")} @标签={(opt.AtUserTags ? "开" : "关")}\n"
            + $"白名单群数：{opt.GroupAllowList.Count}（0=全部）\n"
            + $"累计：富消息 {snap.RichSent} 按钮 {snap.KeyboardSent} "
            + $"文字链 {snap.LinkOnlySent} 降级 {snap.DegradeRetries} 转纯文本 {snap.PlainFallback}";
    }

    private async Task<string> TryProbeAsync(string groupOpenId, string msgId, bool withKeyboard)
    {
        var menu = GameMenuCatalog.For(GameAction.Help, MenuContext.Empty);
        var keyboard = withKeyboard
            ? GameKeyboard.From(menu, GameKeyboardMode.Command, _richUi.Options, _logger)
            : null;

        var probe = new OutboundMessage
        {
            MarkdownText = withKeyboard
                ? "### 富交互自检（按钮）\n若下方出现按钮，说明消息按钮组件可用。"
                : "### 富交互自检（文本）\n本行应为标题样式，说明 markdown 可用。",
            Keyboard = keyboard,
            MsgId = msgId,
            MsgSeq = _seqTracker.Next(msgId),
        };

        try
        {
            await _apiService.SendGroupRichAsync(groupOpenId, probe);
            return "可用";
        }
        catch (QqApiException ex)
        {
            _logger.LogWarning("RichUi 探测失败 withKeyboard={WithKeyboard} err={ErrCode}", withKeyboard, ex.ErrCode);
            return $"不可用（err={ex.ErrCode}）";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RichUi 探测异常");
            return "探测异常";
        }
    }

    private void PruneHandledInteractions(DateTime now)
    {
        if (_handledInteractions.Count <= 2000) return;
        var cutoff = now.AddMinutes(-10);
        foreach (var kv in _handledInteractions)
        {
            if (kv.Value < cutoff)
                _handledInteractions.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>保存 Bot 回复到聊天历史（用于前端 Chat History + Dashboard 统计）。</summary>
    private async Task SaveBotMessageAsync(string? groupOpenId, string content,
        string? aiModel = null, int? promptTokens = null, int? completionTokens = null)
    {
        try
        {
            var botMsg = new ChatMessage
            {
                MessageId = $"bot-{Guid.NewGuid():N}",
                ChatType = groupOpenId is not null ? 0 : 1,
                GroupId = groupOpenId,
                UserId = _botId,
                UserName = "Bot",
                PlainText = content,
                IsBotMessage = true,
                AiModelUsed = aiModel,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                CreatedAt = DateTime.UtcNow
            };
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            await repo.SaveMessageAsync(botMsg);
        }
        catch (Exception ex) { _logger.LogError(ex, "Save bot message failed"); }
    }
}
