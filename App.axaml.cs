using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using MdModManager.Services;
using MdModManager.ViewModels;
using MdModManager.Views;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


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
        RuntimeLog.Reset();
        ConfigureServices();
        
        // 软件启动时后台静默预获取账号与成绩数据
        MuseDashAccountService.StartPrefetch();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configService = Ioc.Default.GetService<IConfigService>();
            if (configService != null)
            {
                configService.Load();
            }
            
            // 异步检测更新
            var updateService = Ioc.Default.GetService<IUpdateService>();
            updateService?.CheckAndApplyUpdateAsync();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Ioc.Default.GetRequiredService<MainWindowViewModel>(),
            };
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

        // Services (will be added here later)
        services.AddSingleton<MdModManager.Services.IConfigService, MdModManager.Services.ConfigService>();
        services.AddSingleton<MdModManager.Services.IGamePathService, MdModManager.Services.GamePathService>();
        services.AddSingleton<MdModManager.Services.IMelonLoaderService, MdModManager.Services.MelonLoaderService>();
        services.AddSingleton<MdModManager.Services.IModCatalogService, MdModManager.Services.ModCatalogService>();
        services.AddSingleton<MdModManager.Services.ILocalModService, MdModManager.Services.LocalModService>();
        services.AddSingleton<MdModManager.Services.INotificationService, MdModManager.Services.NotificationService>();
        services.AddSingleton<MdModManager.Services.IConfigFileService, MdModManager.Services.ConfigFileService>();
        services.AddSingleton<MdModManager.Services.IChartService, MdModManager.Services.ChartService>();
        services.AddSingleton<MdModManager.Services.IChartDownloadService, MdModManager.Services.ChartDownloadService>();
        services.AddSingleton<MdModManager.Services.IDownloadManagerService, MdModManager.Services.DownloadManagerService>();
        services.AddSingleton<MdModManager.Services.INavigationService, MdModManager.Services.NavigationService>();
        services.AddSingleton<MdModManager.Services.ModStagingService>();
        services.AddSingleton<MdModManager.Services.IUpdateService, MdModManager.Services.UpdateService>();
        services.AddSingleton<MdModManager.Services.IAnnouncementService, MdModManager.Services.AnnouncementService>();
        services.AddSingleton<MdModManager.Services.IAlbumCollectionService, MdModManager.Services.AlbumCollectionService>();

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
/* v1.1.1 */
