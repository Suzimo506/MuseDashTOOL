using Avalonia;
using System;

namespace MdModManager;

sealed class Program
{
    // 初始化代码
    [STAThread]
    public static void Main(string[] args)
    {
        bool createdNew;
        System.Threading.Mutex? mutex = null;
        try
        {
            // 使用全局互斥锁以支持跨权限级别检测
            mutex = new System.Threading.Mutex(true, "Global\\MuseDashTOOL-SingleInstance", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 如果遇到权限拒绝说明已存在更高权限的实例运行
            createdNew = false;
        }

        if (!createdNew)
        {
            // 如果有参数则发送参数否则发送激活指令以唤醒主窗口
            var sendArgs = (args != null && args.Length > 0) ? args : new[] { "euterpe://activate" };
            Bootstrapper.SendArgsToPrimaryInstance(sendArgs);
            return;
        }

        Bootstrapper.StartDeepLinkPipeServer();
        
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Bootstrapper.StopDeepLinkPipeServer();
            mutex?.Dispose();
        }
    }

    // Avalonia 配置
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        return builder;
    }
}
