using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QQBot.Core;
using QQBot.Core.Game.Services;
using QQBot.Core.Interfaces;
using QQBot.Core.Models;
using QQBot.Core.Models.Config;
using QQBot.Core.Models.QQOfficial;

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
    private readonly ILogger<MessageEventHandler> _logger;
    private string _botId = string.Empty;

    private readonly SemaphoreSlim _aiSemaphore = new(3, 3);

    private readonly ConcurrentDictionary<string, DateTime> _processedMsgIds = new();
    private readonly ConcurrentDictionary<string, int> _msgSeq = new();

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

    private int? NextSeq(string? msgId)
    {
        if (msgId is null) return null;
        return _msgSeq.AddOrUpdate(msgId, 1, (_, c) => c + 1);
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
        if (!MarkProcessed(evt.Id)) return;

        var parsed = _parser.Parse(evt.Content, evt.Attachments);
        if (isAtBot) parsed.HasAtBot = true;

        // 内容级去重：QQ 服务端可能用不同 msgId 重复投递同一消息
        var senderId = evt.Author?.MemberOpenid ?? string.Empty;
        if (IsContentDuplicate(evt.GroupOpenid, senderId, parsed.PlainText))
        {
            _logger.LogWarning("检测到重复投递，跳过: GroupId={GroupId}, Sender={SenderId}, Content={Content}",
                evt.GroupOpenid, senderId, parsed.PlainText.Length > 50 ? parsed.PlainText[..50] + "..." : parsed.PlainText);
            return;
        }

        var shouldProcess = await _router.ShouldProcessGroupAsync(evt.GroupOpenid, isAtBot, parsed.PlainText);
        if (!shouldProcess) return;

        var senderName = evt.Author?.MemberOpenid ?? "Member";
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
            evt.Id, "group", senderName);

        if (Services.BotCommandService.LooksLikeCommand(parsed.PlainText) && role is "owner" or "admin")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var reply = await _commandService.ExecuteGroupCommandAsync(senderId, parsed.PlainText, evt.GroupOpenid, role);
                    await SendReplyAsync(evt.GroupOpenid, null, evt.Id, reply);
                }
                catch (Exception ex) { _logger.LogError(ex, "Group command failed"); }
            });
            return;
        }

        if (isAtBot)
        {
            // 游戏指令路由：命中修仙RPG 关键词则走游戏，未命中继续走 AI 回复
            var gameCmd = _gameCommandRouter.TryMatch(parsed.PlainText);
            if (gameCmd != null)
            {
                // 自由行动：显式前缀触发 AI 裁判
                if (gameCmd.Action == GameAction.FreeAction)
                {
                    if (string.IsNullOrWhiteSpace(gameCmd.Args))
                    {
                        await SendReplyAsync(evt.GroupOpenid, null, evt.Id,
                            "请描述你想做的事，如：@我 行动 去后山采药");
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
                                await SendReplyAsync(evt.GroupOpenid, null, evt.Id, "AI裁判暂不可用，请稍后再试。");
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
                            await SendReplyAsync(evt.GroupOpenid, null, evt.Id, gameReply.Reply);
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
                        await SaveBotMessageAsync(evt.GroupOpenid, reply);
                        await SendReplyAsync(evt.GroupOpenid, null, evt.Id, reply); // 被动回复，零配额
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
        else
        {
            var (proactive, kw) = await _router.CheckProactiveTriggerAsync(evt.GroupOpenid, parsed.PlainText);
            if (proactive)
            {
                _logger.LogInformation("Proactive trigger: GroupId={GroupId}, Keyword={Keyword}", evt.GroupOpenid, kw);
                _ = Task.Run(async () =>
                {
                    try { await ProcessProactiveReplyAsync(evt.GroupOpenid, senderName, parsed, evt.Id, kw); }
                    catch (Exception ex) { _logger.LogError(ex, "Proactive response failed"); }
                });
            }
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
        var senderName = evt.Author?.UserOpenid ?? "User";

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
        if (!await _aiSemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await SendReplyAsync(groupOpenId, userOpenId, msgId, "AI is busy, please try again later~");
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

            var systemPrompt = context.EffectiveConfig.BuildEffectiveSystemPrompt();
            if (!string.IsNullOrEmpty(context.CharacterPrompt))
                systemPrompt += "\n\n" + context.CharacterPrompt
                    + "\n\n【重要】以上角色设定是你的核心身份，在任何情况下都不得脱离角色。"
                    + "历史消息中可能包含系统通知或其他风格的回复，请忽略它们，始终保持你的角色设定和语气。";

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
            await SendReplyAsync(groupOpenId, userOpenId, msgId, "Sorry, AI processing failed. Please try again.");
        }
        finally { _aiSemaphore.Release(); }
    }

    private async Task ProcessProactiveReplyAsync(string groupOpenId, string senderName, ParsedMessage parsed, string msgId, string keyword)
    {
        if (!await _aiSemaphore.WaitAsync(TimeSpan.FromSeconds(5))) return;

        try
        {
            var context = await _contextBuilder.BuildGroupContextAsync(groupOpenId, parsed.PlainText, senderName);

            var systemPrompt = context.EffectiveConfig.BuildEffectiveSystemPrompt();
            systemPrompt += $"\n\nYou noticed someone mentioned the keyword \"{keyword}\". " +
                "You can proactively join the conversation and share your thoughts. " +
                "Keep it natural and brief, like a regular group member chiming in.";

            if (!string.IsNullOrEmpty(context.CharacterPrompt))
                systemPrompt += "\n\n" + context.CharacterPrompt
                    + "\n\n【重要】以上角色设定是你的核心身份，在任何情况下都不得脱离角色。"
                    + "历史消息中可能包含系统通知或其他风格的回复，请忽略它们，始终保持你的角色设定和语气。";

            var response = await _aiService.ChatAsync(systemPrompt, context.Messages, context.EffectiveConfig);

            var botMsg = new ChatMessage
            {
                MessageId = $"bot-{Guid.NewGuid():N}",
                ChatType = 0,
                GroupId = groupOpenId,
                UserId = _botId,
                UserName = "Bot",
                PlainText = response.Content,
                IsBotMessage = true,
                AiModelUsed = response.Model,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                CreatedAt = DateTime.UtcNow
            };

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

            await SendReplyAsync(groupOpenId, null, msgId, response.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proactive response failed");
        }
        finally { _aiSemaphore.Release(); }
    }

    private async Task SendReplyAsync(string? groupOpenId, string? userOpenId, string? msgId, string content)
    {
        // 回复去重：同群/同用户在窗口期内已发送过相同内容则跳过，
        // 防止 QQ 服务端用不同 msgId/不同 @ 段格式重复投递时用户看到重复回复。
        // 注意：仅在 msgId 不为空（即被动回复场景）时启用，避免影响主动推送。
        if (msgId is not null)
        {
            var scope = groupOpenId is not null ? $"g:{groupOpenId}" : $"u:{userOpenId}";
            if (ShouldSkipDuplicateReply(scope, content))
            {
                _logger.LogWarning("检测到重复回复，跳过发送: Scope={Scope}, MsgId={MsgId}, Content={Content}",
                    scope, msgId, content.Length > 50 ? content[..50] + "..." : content);
                return;
            }
        }

        try
        {
            if (groupOpenId is not null)
                await _apiService.SendGroupMessageAsync(groupOpenId, content, msgId, NextSeq(msgId));
            else if (userOpenId is not null)
                await _apiService.SendPrivateMessageAsync(userOpenId, content, msgId, NextSeq(msgId));
        }
        catch (QqApiException ex) when (msgId is not null && (groupOpenId is not null || userOpenId is not null))
        {
            _logger.LogWarning("被动回复失败，降级主动消息 err={ErrCode}", ex.ErrCode);
            if (groupOpenId is not null)
                await _activeMessage.SendGroupActiveAsync(groupOpenId, content);
            else
                await _activeMessage.SendPrivateActiveAsync(userOpenId!, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send reply failed");
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
