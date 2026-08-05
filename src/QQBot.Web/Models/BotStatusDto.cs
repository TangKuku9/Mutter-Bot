namespace QQBot.Web.Models;

public class BotStatusDto
{
    public bool IsConnected { get; set; }
    public string BotQQ { get; set; } = string.Empty;
    public string BotNickname { get; set; } = string.Empty;
    public string Uptime { get; set; } = string.Empty;
    public int WebPanelPort { get; set; }
}
