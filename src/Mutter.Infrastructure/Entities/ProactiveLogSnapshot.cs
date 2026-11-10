using System.Text.Json;
using Mutter.Core.Proactive;

namespace Mutter.Infrastructure.Entities;

/// <summary>只保存本次观察窗口，不保存模型提示词、内部推理或临时图片链接。</summary>
public sealed record ProactiveLogSnapshot(int Version, int OmittedMessages, ProactiveSnapshotMessage[] Messages)
{
    public static string Capture(ProactiveWindow window)
    {
        const int maxMessages = 50, maxText = 2000;
        return JsonSerializer.Serialize(new ProactiveLogSnapshot(1, Math.Max(0, window.Messages.Count - maxMessages),
            window.Messages.TakeLast(maxMessages).Select(m => new ProactiveSnapshotMessage(
                m.SenderName, m.Text.Length > maxText ? m.Text[..maxText] : m.Text,
                m.MessageId, m.UtcAt, m.ImageUrls?.Count > 0, m.Text.Length > maxText)).ToArray()));
    }
}

public sealed record ProactiveSnapshotMessage(string SenderName, string Text, string MessageId,
    DateTime UtcAt, bool HasImage, bool Truncated);
