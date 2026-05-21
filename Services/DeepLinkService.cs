using System;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Win32;

namespace MdModManager.Services;

public sealed class DeepLinkService
{
    private const string DeepLinkScheme = "euterpe";
    private readonly IAuthService _authService;

    public DeepLinkService(IAuthService authService)
    {
        _authService = authService;
    }

    public Task SetupAsync()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath))
            {
                return Task.CompletedTask;
            }

            using var schemeKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{DeepLinkScheme}");
            schemeKey.SetValue(string.Empty, "URL:Euterpe Protocol", RegistryValueKind.String);
            schemeKey.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

            using var commandKey = schemeKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(string.Empty, $"\"{processPath}\" \"%1\"", RegistryValueKind.String);

            using var iconKey = schemeKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue(string.Empty, $"\"{processPath}\",0", RegistryValueKind.String);
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
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != DeepLinkScheme)
        {
            return;
        }

        ActivateMainWindow(true);

        var action = parsed.Host;
        var query = Uri.UnescapeDataString(parsed.Query.TrimStart('?'));

        if (action == "auth")
        {
            _ = HandleAuthCallbackAsync(query);
        }
    }

    private async Task HandleAuthCallbackAsync(string query)
    {
        var queryParams = HttpUtility.ParseQueryString(query);
        var code = queryParams["code"];

        if (string.IsNullOrEmpty(code))
        {
            await _authService.LoginAsync();
            return;
        }

        try
        {
            await _authService.CompleteLoginAsync(code);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            await _authService.LoginAsync();
        }
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
