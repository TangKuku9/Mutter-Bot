using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mutter.Core.Models;
using Mutter.Infrastructure.Data;

namespace Mutter.Infrastructure.Services;

public class MessageWriterService : BackgroundService
{
    private readonly ChannelReader<ChatMessage> _reader;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MessageWriterService> _logger;

    public MessageWriterService(
        ChannelReader<ChatMessage> reader,
        IServiceProvider serviceProvider,
        ILogger<MessageWriterService> logger)
    {
        _reader = reader;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MessageWriterService 已启动");

        while (await _reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<ChatMessage>();
            while (_reader.TryRead(out var msg))
                batch.Add(msg);

            if (batch.Count == 0) continue;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 去重：过滤已存在的 MessageId（数据库侧）
                var messageIds = batch
                    .Where(m => !string.IsNullOrEmpty(m.MessageId))
                    .Select(m => m.MessageId)
                    .ToHashSet();

                var existingIds = messageIds.Count > 0
                    ? await db.ChatMessages
                        .Where(m => messageIds.Contains(m.MessageId))
                        .Select(m => m.MessageId)
                        .ToListAsync(stoppingToken)
                    : new List<string>();

                var existingSet = existingIds as ICollection<string> ?? existingIds.ToList();
                var seenInBatch = new HashSet<string>();
                var newMessages = new List<ChatMessage>();

                // 去重：同批次内按 MessageId 去重 + 过滤数据库已存在
                // 空 MessageId 不参与去重（数据库 UNIQUE 索引有 filter "MessageId" <> ''）
                foreach (var m in batch)
                {
                    if (string.IsNullOrEmpty(m.MessageId))
                    {
                        newMessages.Add(m);
                        continue;
                    }

                    if (existingSet.Contains(m.MessageId))
                        continue;

                    if (!seenInBatch.Add(m.MessageId))
                        continue;

                    newMessages.Add(m);
                }

                if (newMessages.Count > 0)
                {
                    await db.ChatMessages.AddRangeAsync(newMessages, stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogDebug("已写入 {Count} 条消息到数据库", newMessages.Count);
                }
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // UNIQUE 约束失败：通常是并发写入或重复消息，降级逐条写入（会跳过已存在）
                _logger.LogWarning("批量写入遇到重复 MessageId，降级逐条写入，批次大小: {BatchSize}", batch.Count);
                await FallbackWriteOneByOneAsync(batch, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量写入消息失败，降级为逐条写入，批次大小: {BatchSize}", batch.Count);
                await FallbackWriteOneByOneAsync(batch, stoppingToken);
            }
        }

        _logger.LogInformation("MessageWriterService 已停止");
    }

    /// <summary>
    /// 判断是否为 UNIQUE 约束冲突（SQLite Error 19）
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var e = ex.InnerException; e is not null; e = (e as Exception)?.InnerException)
        {
            if (e is Microsoft.Data.Sqlite.SqliteException sqliteEx &&
                sqliteEx.SqliteErrorCode == 19)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 批量写入失败时逐条重试，单条失败只丢弃该条，避免整批消息丢失
    /// </summary>
    private async Task FallbackWriteOneByOneAsync(List<ChatMessage> batch, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        foreach (var msg in batch)
        {
            // 同一批次内已处理的非空 MessageId 跳过
            if (!string.IsNullOrEmpty(msg.MessageId) && !seen.Add(msg.MessageId))
                continue;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                if (!string.IsNullOrEmpty(msg.MessageId) &&
                    await db.ChatMessages.AnyAsync(m => m.MessageId == msg.MessageId, ct))
                    continue;

                db.ChatMessages.Add(msg);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // 并发场景下重复，静默跳过
                _logger.LogDebug("消息已存在，跳过: MsgId={MessageId}", msg.MessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "单条消息写入失败，已丢弃: MsgId={MessageId}, User={UserName}",
                    msg.MessageId, msg.UserName);
            }
        }
    }
}
