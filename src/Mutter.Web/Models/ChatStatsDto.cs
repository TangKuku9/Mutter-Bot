namespace Mutter.Web.Models;

public class ChatStatsDto
{
    public long TotalMessages { get; set; }
    public long TodayMessages { get; set; }
    public long AiCallCount { get; set; }
    public long TotalTokensUsed { get; set; }
}
