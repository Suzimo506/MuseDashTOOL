using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
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
        if (TryParseGlobalSearchRequest(parsed, out var request))
        {
            Ioc.Default.GetRequiredService<INavigationService>().RequestNavigateToGlobalChartSearch(request);
        }
    }

    private static bool TryParseGlobalSearchRequest(Uri uri, out MdenGlobalSearchRequest request)
    {
        request = null!;

        var command = string.IsNullOrWhiteSpace(uri.Host)
            ? uri.AbsolutePath.Trim('/')
            : uri.Host;
        if (!string.Equals(command, "global-search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = GetQueryValue(uri.Query, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        _ = int.TryParse(GetQueryValue(uri.Query, "difficulty"), out var difficulty);
        request = new MdenGlobalSearchRequest(
            query.Trim(),
            NormalizeOptional(GetQueryValue(uri.Query, "chartKey")),
            difficulty,
            NormalizeOptional(GetQueryValue(uri.Query, "artist")),
            NormalizeOptional(GetQueryValue(uri.Query, "charter")));
        return true;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var value = query[0] == '?' ? query[1..] : query;
        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var name = Uri.UnescapeDataString(pair[0].Replace("+", " "));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return pair.Length > 1
                ? Uri.UnescapeDataString(pair[1].Replace("+", " "))
                : string.Empty;
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
