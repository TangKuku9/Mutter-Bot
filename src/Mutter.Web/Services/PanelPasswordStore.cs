using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Mutter.Web.Services;

/// <summary>更新实际提供面板密码的配置文件，并同步重新加载，避免首次改密后仍读取旧口令。</summary>
public sealed class PanelPasswordStore(IConfiguration configuration)
{
    public void Save(string storedHash)
    {
        if (configuration is not IConfigurationRoot root)
            throw new InvalidOperationException("当前密码配置不支持写入，请通过部署配置修改密码。");

        // ContentRoot 与 AppContext.BaseDirectory 在 dotnet run 下通常不同。
        // 也不能固定写基础文件：环境专属 JSON 可能已覆盖 PanelAuth:Password。
        var provider = root.Providers.Reverse().FirstOrDefault(provider =>
            provider.TryGet("PanelAuth:Password", out _));
        if (provider is not JsonConfigurationProvider jsonProvider ||
            jsonProvider.Source.Path is not { } sourcePath ||
            jsonProvider.Source.FileProvider?.GetFileInfo(sourcePath).PhysicalPath is not { } settingsPath)
            throw new InvalidOperationException("当前密码由环境变量或非文件配置提供，请通过对应部署配置修改密码。");

        var document = JsonNode.Parse(File.ReadAllText(settingsPath)) ?? new JsonObject();
        document["PanelAuth"] ??= new JsonObject();
        document["PanelAuth"]!["Password"] = storedHash;
        // 写入原文件，保留安装时设置的权限/ACL，不以新文件替换凭据配置。
        File.WriteAllText(settingsPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        // 文件监视器异步通知；改密响应返回前主动加载，使下一次登录立即可用。
        root.Reload();
    }
}
