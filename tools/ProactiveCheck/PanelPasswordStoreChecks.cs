using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Mutter.Web.Controllers;
using Mutter.Web.Services;

internal static class PanelPasswordStoreChecks
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== 3g2. 面板密码保存路径与即时生效 ==");
        var directory = Path.Combine(Path.GetTempPath(), "mutter-password-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "appsettings.json");
            var environmentPath = Path.Combine(directory, "appsettings.Acceptance.json");
            File.WriteAllText(settingsPath, """{"PanelAuth":{"Username":"admin","Password":"initial-password","SessionTimeoutMinutes":120},"Other":{"Keep":true}}""");
            using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false, reloadOnChange: false).Build();
            var hash = PanelPasswordHasher.Hash("new-user-password");
            new PanelPasswordStore(configuration).Save(hash);
            var saved = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            check(saved["PanelAuth"]!["Password"]!.GetValue<string>() == hash,
                "内容目录与运行目录不同时，密码写入真正加载的配置文件");
            check(saved["PanelAuth"]!["Username"]!.GetValue<string>() == "admin" &&
                saved["PanelAuth"]!["SessionTimeoutMinutes"]!.GetValue<int>() == 120 && saved["Other"]!["Keep"]!.GetValue<bool>(),
                "修改密码保留账号、会话设置与其他配置");
            check(PanelPasswordHasher.Verify(configuration["PanelAuth:Password"], "new-user-password") &&
                !PanelPasswordHasher.Verify(configuration["PanelAuth:Password"], "initial-password"),
                "无需文件监视器或重启，保存后立即只接受新密码");

            File.WriteAllText(environmentPath, """{"PanelAuth":{"Password":"environment-password"},"EnvironmentSetting":true}""");
            using var overridden = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false, reloadOnChange: false)
                .AddJsonFile(environmentPath, optional: false, reloadOnChange: false).Build();
            var baseBefore = File.ReadAllText(settingsPath);
            new PanelPasswordStore(overridden).Save(hash);
            check(JsonNode.Parse(File.ReadAllText(environmentPath))!["PanelAuth"]!["Password"]!.GetValue<string>() == hash &&
                File.ReadAllText(settingsPath) == baseBefore,
                "环境专属配置覆盖密码时，更新覆盖文件而非基础文件");
            check(PanelPasswordHasher.Verify(overridden["PanelAuth:Password"], "new-user-password"),
                "环境专属密码修改后立即生效");

            using var readOnly = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false, reloadOnChange: false)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["PanelAuth:Password"] = "externally-managed-password" }).Build();
            var rejected = false;
            try { new PanelPasswordStore(readOnly).Save(hash); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected && readOnly["PanelAuth:Password"] == "externally-managed-password" &&
                File.ReadAllText(settingsPath) == baseBefore,
                "外部配置覆盖密码时明确拒绝写入，不虚报改密成功");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
