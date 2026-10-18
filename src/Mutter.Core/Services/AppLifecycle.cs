namespace Mutter.Core.Services;

/// <summary>
/// Web 面板与宿主进程之间的生命周期信号。自更新暂存完成后由面板触发重启，
/// ConsoleHost 收到后停止服务，并在 Linux 上应用暂存文件后退出（systemd 会拉起新版本）。
/// </summary>
public static class AppLifecycle
{
    public static event Action? RestartRequested;

    public static void RequestRestart() => RestartRequested?.Invoke();
}
