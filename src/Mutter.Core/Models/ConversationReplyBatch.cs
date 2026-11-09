namespace Mutter.Core.Models;

/// <summary>尚未被 worker 领取的回复输入；合并和领取由会话队列的锁保护。</summary>
public sealed class ConversationReplyBatch(ChatMessage message, ParsedMessage parsed)
{
    private const int MaxMessages = 64;
    private readonly List<ChatMessage> _messages = [message];
    private readonly List<string> _images = [.. parsed.ImageUrls];

    public IReadOnlyList<ChatMessage> Messages => _messages;
    public ChatMessage Latest => _messages[^1];
    public ParsedMessage Input => new()
    {
        PlainText = Latest.PlainText,
        ImageUrls = [.. _images],
        HasImage = _images.Count > 0
    };

    public bool TryMerge(ConversationReplyBatch other)
    {
        // 热群持续来消息也不能无限持有输入；满批后正常排下一项。
        if (_messages.Count + other._messages.Count > MaxMessages) return false;
        _messages.AddRange(other._messages);
        _images.AddRange(other._images);
        return true;
    }
}
