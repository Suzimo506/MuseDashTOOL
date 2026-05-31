using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Win32;

namespace MdModManager.Services;

public sealed class DeepLinkService
{
    // 旧版本曾把 "euterpe://" 注册为系统协议处理器用于 OAuth 回调；新登录流程
    // 改用 RFC 8252 loopback，不再需要任何系统 URL scheme。这里仅用于在启动时
    // 把遗留的 "euterpe://" 注册还给其正主。
    private const string LegacyScheme = "euterpe";

    // 单实例激活哨兵：第二个实例通过命名管道把该串发给主实例以唤醒窗口。
    // 走管道而非系统 scheme，因此无需注册到注册表。
    private const string ActivationScheme = "musedashtool";

    public Task SetupAsync()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath))
            {
                return Task.CompletedTask;
            }

            // 仅当遗留的 "euterpe://" 处理器指向本程序时才删除，避免误删他人注册。
            using var commandKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{LegacyScheme}\shell\open\command");
            if (commandKey?.GetValue(string.Empty) is string command &&
                command.Contains(processPath, StringComparison.OrdinalIgnoreCase))
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{LegacyScheme}", throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        return Task.CompletedTask;
    }

    public void HandleStartupArgs(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            return;
        }

        HandleUri(args[0]);
    }

    public void HandleUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != ActivationScheme)
        {
            return;
        }

        ActivateMainWindow(true);
    }

    private void ActivateMainWindow(bool force)
    {
        var app = Application.Current;
        if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                if (mainWindow.WindowState == WindowState.Minimized)
                {
                    mainWindow.WindowState = WindowState.Normal;
                }

                if (force)
                {
                    mainWindow.Topmost = true;
                    mainWindow.Topmost = false;
                }

                Dispatcher.UIThread.Post(mainWindow.Activate);
            }
        }
    }
}
