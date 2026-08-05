namespace QQBot.Core.Configuration;

public class QQBotOptions
{
    public string AppId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.bot.qq.com";
}
