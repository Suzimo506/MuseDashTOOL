using Avalonia;
using System;

namespace MdModManager;

sealed class Program
{
    // 初始化代码
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "MuseDashTOOL-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            if (args != null && args.Length > 0)
            {
                Bootstrapper.SendArgsToPrimaryInstance(args);
            }
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
