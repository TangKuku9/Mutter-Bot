using System.Text.Encodings.Web;
using System.Text.Json;
using Mutter.Core.Proactive;

namespace Mutter.Infrastructure.Entities;

/// <summary>
/// 只保存本次观察窗口，不保存模型提示词、内部推理或临时图片链接。
/// 写入方只在群设置为「围观并记录全部消息」且真正进入模型评估时调用；快照是详情附件，
/// 序列化失败返回 null，不能连累整条决策留痕。
/// </summary>
public sealed record ProactiveLogSnapshot(int Version, int OmittedMessages, ProactiveSnapshotMessage[] Messages)
{
    public const int CurrentVersion = 1;
    public const int MaxMessages = 50;
    public const int MaxTextChars = 2000;
    /// <summary>全部消息正文的字符预算，避免 50 条长文把单条留痕撑到几百 KB。</summary>
    public const int MaxTotalTextChars = 20_000;

    // 快照存进 SQLite 后由 Vue 以文本插值渲染，不做 HTML 拼接；不转义中文与 emoji，
    // 既省空间也避免前端把 \uXXXX 原样显示。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string? Capture(ProactiveWindow window)
    {
        try
        {
            var messages = window.Messages;
            if (messages.Count == 0)
                return JsonSerializer.Serialize(new ProactiveLogSnapshot(CurrentVersion, 0, []), JsonOptions);

            var start = Math.Max(0, messages.Count - MaxMessages);
            var omitted = start;
            var selected = new List<ProactiveSnapshotMessage>(Math.Min(messages.Count, MaxMessages));
            var totalChars = 0;

            // 从最新往回取，预算不够时丢最旧的；这样详情里留下的永远是最近一段。
            for (var i = messages.Count - 1; i >= start; i--)
            {
                var text = Truncate(messages[i].Text, MaxTextChars);
                if (selected.Count > 0 && totalChars + text.Length > MaxTotalTextChars)
                {
                    omitted += i - start + 1;
                    break;
                }

                selected.Add(new ProactiveSnapshotMessage(
                    messages[i].SenderName, text, messages[i].MessageId, messages[i].UtcAt,
                    messages[i].ImageUrls?.Count > 0, messages[i].Text.Length > MaxTextChars));
                totalChars += text.Length;
            }

            selected.Reverse();
            return JsonSerializer.Serialize(new ProactiveLogSnapshot(CurrentVersion, omitted, selected.ToArray()), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>按 UTF-16 长度截断，但不能把 emoji 的代理对切成半个字符。</summary>
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= max) return text;

        var length = max;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length];
    }
}

public sealed record ProactiveSnapshotMessage(string SenderName, string Text, string MessageId,
    DateTime UtcAt, bool HasImage, bool Truncated);
