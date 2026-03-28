using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Services;
using MdModManager.ViewModels;
using MdModManager.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MdModManager;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configService = Ioc.Default.GetService<IConfigService>();
            var gamePathService = Ioc.Default.GetService<IGamePathService>();

            if (configService != null)
            {
                configService.Load();

                // 运行日志优先放在喵斯快跑游戏根目录，方便直接在游戏目录里查看。
                // 如果配置里没有有效游戏路径，就尝试自动探测一次；写入失败时会自动回退到 LocalAppData。
                var configuredGamePath = configService.Config.GamePath;
                if ((gamePathService == null || !gamePathService.IsValidGamePath(configuredGamePath)) && gamePathService != null)
                {
                    configuredGamePath = gamePathService.DetectGamePath() ?? configuredGamePath;
                }

                RuntimeLog.Configure(configuredGamePath);
            }
            else
            {
                RuntimeLog.Configure(null);
            }

            RuntimeLog.Reset();

            // 软件启动时后台静默预获取账号与成绩数据。
            MuseDashAccountService.StartPrefetch();

            var updateService = Ioc.Default.GetService<IUpdateService>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Ioc.Default.GetRequiredService<MainWindowViewModel>(),
            };

            // 等主窗口真正创建完成后再启动更新检查，
            // 避免更新包下载完成时因为没有可用的 owner 窗口而无法弹出确认对话框。
            desktop.MainWindow.Opened += (s, e) =>
            {
                updateService?.CheckAndApplyUpdateAsync();
            };

            desktop.Exit += (s, e) =>
            {
                updateService?.ApplyPendingUpdate();
            };
        }
        else
        {
            RuntimeLog.Configure(null);
            RuntimeLog.Reset();
            MuseDashAccountService.StartPrefetch();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MelonLoaderViewModel>();
        services.AddSingleton<ModManagerViewModel>();
        services.AddTransient<TutorialViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ConfigManagerViewModel>();
        services.AddTransient<ChartManagerViewModel>();
        services.AddSingleton<ChartDownloadViewModel>();
        services.AddTransient<DownloadManagerViewModel>();
        services.AddTransient<AccountViewModel>();
        services.AddSingleton<AlbumCollectionViewModel>();
        services.AddTransient<AlbumDetailViewModel>();
        services.AddTransient<CommunityCategoryDetailViewModel>();

        // Services
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IGamePathService, GamePathService>();
        services.AddSingleton<IMelonLoaderService, MelonLoaderService>();
        services.AddSingleton<IModCatalogService, ModCatalogService>();
        services.AddSingleton<ILocalModService, LocalModService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IConfigFileService, ConfigFileService>();
        services.AddSingleton<IChartService, ChartService>();
        services.AddSingleton<IChartDownloadService, ChartDownloadService>();
        services.AddSingleton<IDownloadManagerService, DownloadManagerService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ModStagingService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAnnouncementService, AnnouncementService>();
        services.AddSingleton<IAlbumCollectionService, AlbumCollectionService>();

        Ioc.Default.ConfigureServices(services.BuildServiceProvider());
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
