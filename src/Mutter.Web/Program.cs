using Microsoft.EntityFrameworkCore;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure.Repositories;
using Mutter.Core.Interfaces;
using Mutter.Web;
using Mutter.Web.Middleware;
using Mutter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 注册 DbContext
builder.Services.AddDbContext<AppDbContext>();

// 注册 Repository
builder.Services.AddScoped<IMessageRepository, MessageRepository>();

// 注册 Web 层服务（Controllers、SignalR、CORS）
builder.Services.AddMutterWeb();

// 注册管理面板会话存储（内存单例）
var sessionTimeout = TimeSpan.FromMinutes(
    builder.Configuration.GetValue("PanelAuth:SessionTimeoutMinutes", 120));
builder.Services.AddSingleton(new PanelSessionStore(sessionTimeout));
builder.Services.AddSingleton<PanelLoginGuard>();

var app = builder.Build();

// 确保数据库已创建
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await dbContext.EnsureSchemaUpgradedAsync();
}

// 配置中间件管道
app.UseMiddleware<ApiAuthMiddleware>();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<Mutter.Web.Hubs.BotStatusHub>("/hub/botstatus");

// 监听 0.0.0.0:5188（支持 LAN 访问）
await app.RunAsync("http://0.0.0.0:5188");
