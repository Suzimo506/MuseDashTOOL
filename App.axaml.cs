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
using MdModManager.Models;
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
            if (configService != null)
            {
                configService.Load();
                MuseDashAccountService.Configure(configService);
                
                // 初始化多语言
                I18nService.Instance.LoadLanguage(configService.Config.Language);

                // 主窗口创建前只使用本地日志目录。Steam 库可能含失效盘符，路径探测必须等窗口显示后进行。
                RuntimeLog.Configure(null);
            }
            else
            {
                RuntimeLog.Configure(null);
            }

            RuntimeLog.Reset();
            RuntimeLog.Write("App", "基础启动配置完成，准备创建主窗口。");

            var mainWindow = new MainWindow();
            RuntimeLog.Write("App", "主窗口 XAML 已创建，准备解析主界面数据。");

            mainWindow.DataContext = Ioc.Default.GetRequiredService<MainWindowViewModel>();
            RuntimeLog.Write("App", "主界面数据已解析，准备显示主窗口。");

            desktop.MainWindow = mainWindow;

            // 所有网络、注册表和磁盘扫描都等窗口打开后执行，避免其中任一项卡住首帧。
            mainWindow.Opened += (s, e) =>
            {
                RuntimeLog.Write("App", "主窗口已打开，开始后台初始化。");
                _ = InitializeMainWindowAsync(mainWindow);
                _ = RunDeferredStartupAsync(desktop);
            };

            desktop.Exit += (s, e) =>
            {
                Ioc.Default.GetService<IUpdateService>()?.ApplyPendingUpdate();
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

    private static async System.Threading.Tasks.Task RunDeferredStartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var mirrorDomainService = Ioc.Default.GetService<IMirrorDomainService>();
            if (mirrorDomainService != null)
            {
                await System.Threading.Tasks.Task.Run(mirrorDomainService.Initialize);
                _ = mirrorDomainService.RefreshFromRemoteIfNeededAsync();
            }

            MuseDashAccountService.StartPrefetch();

            var deepLinkService = Ioc.Default.GetRequiredService<DeepLinkService>();
            await System.Threading.Tasks.Task.Run(deepLinkService.SetupAsync);

            if (desktop.Args is { Length: > 0 })
            {
                deepLinkService.HandleStartupArgs(desktop.Args);
            }

            var telemetryService = Ioc.Default.GetRequiredService<ITelemetryService>();
            var authState = Ioc.Default.GetRequiredService<AuthState>();
            authState.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AuthState.CurrentUser) && authState.CurrentUser != null)
                {
                    _ = telemetryService.BindVanillaAccountAsync();
                }
            };

            _ = telemetryService.TrackSessionAsync();
            _ = Ioc.Default.GetRequiredService<IAuthService>().RestoreSessionAsync();
            _ = Ioc.Default.GetService<IUpdateService>()?.CheckAndApplyUpdateAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("App", $"窗口打开后的初始化失败：{ex}");
        }
    }

    private static async System.Threading.Tasks.Task InitializeMainWindowAsync(MainWindow mainWindow)
    {
        try
        {
            if (mainWindow.DataContext is MainWindowViewModel viewModel)
            {
                RuntimeLog.Write("App", "开始初始化主界面内容。");
                await viewModel.InitializeAsync();
                RuntimeLog.Write("App", "主界面内容初始化完成。");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("App", $"主界面内容初始化失败：{ex}");
        }
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MelonLoaderViewModel>();
        services.AddSingleton<ModManagerViewModel>();
        services.AddTransient<TutorialViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<SponsorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ConfigManagerViewModel>();
        services.AddTransient<ChartManagerViewModel>();
        services.AddTransient<ReplayManagerViewModel>();
        services.AddSingleton<ReplayService>();
        services.AddTransient<ChartUploadViewModel>();
        services.AddSingleton<ChartDownloadViewModel>();
        services.AddTransient<GlobalChartSearchViewModel>();
        services.AddTransient<DownloadManagerViewModel>();
        services.AddTransient<AccountViewModel>();
        services.AddSingleton<AlbumCollectionViewModel>();
        services.AddTransient<AlbumDetailViewModel>();
        services.AddTransient<CommunityCategoryDetailViewModel>();
        services.AddTransient<EuterpeViewModel>();
        services.AddTransient<OnlineLobbyViewModel>();

        // Services
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IGamePathService, GamePathService>();
        services.AddSingleton<IMelonLoaderService, MelonLoaderService>();
        services.AddSingleton<IModCatalogService, ModCatalogService>();
        services.AddSingleton<ILocalModService, LocalModService>();
        services.AddSingleton<BetterMdConflictService>();
        services.AddSingleton<IModUpdateService, ModUpdateService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IConfigFileService, ConfigFileService>();
        services.AddSingleton<IChartService, ChartService>();
        services.AddSingleton<IChartIndexService, ChartIndexService>();
        services.AddSingleton<IChartPackageProcessor, ChartPackageProcessor>();
        services.AddSingleton<IChartDownloadService, ChartDownloadService>();
        services.AddSingleton<IChartUploadConfigService, ChartUploadConfigService>();
        services.AddSingleton<IChartUploadService, ChartUploadService>();
        services.AddSingleton<IDownloadManagerService, DownloadManagerService>();
        services.AddSingleton<IEuterpeChartDownloadService, EuterpeChartDownloadService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ModStagingService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAnnouncementService, AnnouncementService>();
        services.AddSingleton<INewsService, NewsService>();
        services.AddSingleton<ISponsorService, SponsorService>();
        services.AddSingleton<IAlbumCollectionService, AlbumCollectionService>();
        services.AddTransient<IGlobalChartSearchService, GlobalChartSearchService>();
        services.AddSingleton<IMirrorDomainService, MirrorDomainService>();
        services.AddSingleton<IEnsembleLobbyService, EnsembleLobbyService>();

        services.AddSingleton<AuthState>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddTransient<AuthHeaderHandler>();
        services.AddTransient<EuterpeTokenQueryHandler>();
        services.AddSingleton<DeepLinkService>();
        services.AddSingleton<ITelemetryService, TelemetryService>();

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
