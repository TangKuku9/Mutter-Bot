using Microsoft.EntityFrameworkCore;
using Mutter.Infrastructure.Data;
using Mutter.Infrastructure;
using Mutter.Core;
using Mutter.Infrastructure.Repositories;
using Mutter.Core.Interfaces;
using Mutter.Web;
using Mutter.Web.Middleware;
using Mutter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 两种宿主使用相同的运行时装配。
builder.Services.AddCoreServices();
builder.Services.AddInfrastructureServices();

// 注册 Web 层与统一面板安全策略
builder.Services.AddMutterWeb();

var app = builder.Build();

// 确保数据库已创建
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await dbContext.EnsureSchemaUpgradedAsync();
}

// 配置中间件管道
app.UseMutterWeb();

// 尊重部署配置中的监听地址与 HTTPS 端点。
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && !builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
    app.Urls.Add("http://127.0.0.1:5188");
await app.RunAsync();
