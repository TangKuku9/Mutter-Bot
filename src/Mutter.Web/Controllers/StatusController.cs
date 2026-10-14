using Microsoft.AspNetCore.Mvc;
using Mutter.Core.Interfaces;
using Mutter.Web.Models;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly IQqGatewayConnection _connection;
    private readonly IQqBotApiService _apiService;
    private static readonly DateTime StartTime = DateTime.UtcNow;

    public StatusController(IQqGatewayConnection connection, IQqBotApiService apiService)
    {
        _connection = connection;
        _apiService = apiService;
    }

    [HttpGet]
    public Task<ActionResult<BotStatusDto>> GetStatus()
    {
        var uptime = DateTime.UtcNow - StartTime;

        return Task.FromResult<ActionResult<BotStatusDto>>(Ok(new BotStatusDto
        {
            IsConnected = _connection.IsConnected,
            BotQQ = _apiService.BotId,
            BotNickname = _apiService.BotName,
            Uptime = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s",
            WebPanelPort = 5188
        }));
    }
}
