using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MdModManager.ViewModels;

// 欢迎页视图模型
public partial class WelcomeViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly IGamePathService? _gamePathService;
    private readonly ILocalModService? _localModService;
    private readonly IChartService? _chartService;
    private readonly INotificationService? _notificationService;
    private readonly IModCatalogService? _modCatalogService;
    private readonly INewsService? _newsService;

    // 问候语
    [ObservableProperty]
    private string _greetingText = "欢迎回来";

    // 副标题
    [ObservableProperty]
    private string _subtitleText = "您的 Muse Dash 专属助手";

    // 游戏路径
    [ObservableProperty]
    private string _gamePath = "";

    // 游戏路径是否有效
    [ObservableProperty]
    private bool _isGamePathValid;

    // MelonLoader 是否已安装
    [ObservableProperty]
    private bool _isMelonLoaderInstalled;

    // MelonLoader 版本号
    [ObservableProperty]
    private string _melonLoaderVersion = "";

    // 本地 Mod 总数
    [ObservableProperty]
    private int _totalMods;

    // 已启用 Mod 数
    [ObservableProperty]
    private int _enabledMods;

    // 已禁用 Mod 数
    [ObservableProperty]
    private int _disabledMods;

    // 本地谱面总数
    [ObservableProperty]
    private int _totalCharts;

    // 新闻标题
    [ObservableProperty]
    private string _newsTitle = "";

    // 新闻正文
    [ObservableProperty]
    private string _newsContent = "";

    // 新闻链接
    [ObservableProperty]
    private string _newsUrl = "";

    // 是否有新闻
    [ObservableProperty]
    private bool _hasNews;

    // 是否有新闻链接
    public bool HasNewsUrl => !string.IsNullOrWhiteSpace(NewsUrl);

    public WelcomeViewModel()
    {
        _configService = Ioc.Default.GetService<IConfigService>();
        _gamePathService = Ioc.Default.GetService<IGamePathService>();
        _localModService = Ioc.Default.GetService<ILocalModService>();
        _chartService = Ioc.Default.GetService<IChartService>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        _modCatalogService = Ioc.Default.GetService<IModCatalogService>();
        _newsService = Ioc.Default.GetService<INewsService>();
    }

    // 初始化欢迎页数据
    public async Task InitializeAsync()
    {
        UpdateGreeting();
        _ = LoadNewsAsync();
        await LoadStatsAsync();
    }

    // 根据系统时间设置问候语
    private void UpdateGreeting()
    {
        var hour = DateTime.Now.Hour;
        var timeGreeting = hour switch
        {
            >= 5 and < 12 => "早上好",
            >= 12 and < 14 => "中午好",
            >= 14 and < 18 => "下午好",
            _ => "晚上好"
        };

        // 尝试从注册表读取 Steam 用户名
        try
        {
            var steamName = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Valve\Steam")
                ?.GetValue("LastGameNameUsed") as string;
            if (!string.IsNullOrEmpty(steamName))
            {
                var bytes = System.Text.Encoding.GetEncoding(0).GetBytes(steamName);
                steamName = System.Text.Encoding.UTF8.GetString(bytes);
                GreetingText = $"{timeGreeting}，{steamName}";
                return;
            }
        }
        catch
        {
            // 注册表读取失败则回退
        }

        GreetingText = $"{timeGreeting}，欢迎回来";
    }

    // 异步加载新闻
    private async Task LoadNewsAsync()
    {
        if (_newsService == null) return;

        try
        {
            var news = await _newsService.GetLatestNewsAsync();
            if (news != null && !string.IsNullOrWhiteSpace(news.Title))
            {
                NewsTitle = news.Title;
                NewsContent = news.Content;
                NewsUrl = news.Url;
                HasNews = true;
                OnPropertyChanged(nameof(HasNewsUrl));
            }
        }
        catch
        {
            // 新闻加载失败不影响主流程
        }
    }

    // 异步加载统计数据
    private async Task LoadStatsAsync()
    {
        if (_configService == null) return;
        var gamePath = _configService.Config.GamePath;
        GamePath = gamePath;
        IsGamePathValid = _gamePathService != null && _gamePathService.IsValidGamePath(gamePath);

        if (!IsGamePathValid) return;

        await Task.Run(() =>
        {
            // 检测 MelonLoader 版本
            var mlService = Ioc.Default.GetService<IMelonLoaderService>();
            var mlVersion = mlService?.GetCurrentVersion();
            IsMelonLoaderInstalled = !string.IsNullOrEmpty(mlVersion);
            MelonLoaderVersion = mlVersion ?? "未安装";

            // 统计 Mod 数量
            try
            {
                var modsDir = Path.Combine(gamePath, "Mods");
                if (Directory.Exists(modsDir))
                {
                    var dllFiles = Directory.GetFiles(modsDir, "*.dll");
                    var disabledFiles = Directory.GetFiles(modsDir, "*.disabled");
                    EnabledMods = dllFiles.Length;
                    DisabledMods = disabledFiles.Length;
                    TotalMods = EnabledMods + DisabledMods;
                }
            }
            catch
            {
                // 目录访问失败时保持默认值
            }

            // 统计谱面数量
            try
            {
                var chartsDir = Path.Combine(gamePath, "Custom_Albums");
                if (Directory.Exists(chartsDir))
                {
                    TotalCharts = Directory.GetFiles(chartsDir, "*.mdm").Length;
                }
            }
            catch
            {
                // 目录访问失败时保持默认值
            }
        });
    }

    // 快捷导航命令
    [RelayCommand]
    private async Task NavigateToAsync(string target)
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow?.DataContext is not MainWindowViewModel mainVm) return;

        switch (target)
        {
            case "ModManager":
                await mainVm.NavigateToModManagerCommand.ExecuteAsync(null);
                break;
            case "ChartManager":
                await mainVm.NavigateToChartManagerCommand.ExecuteAsync(null);
                break;
            case "ChartDownload":
                await mainVm.NavigateToChartDownloadCommand.ExecuteAsync(null);
                break;
            case "Tutorial":
                await mainVm.NavigateToTutorialCommand.ExecuteAsync(null);
                break;
            case "Settings":
                await mainVm.NavigateToSettingsCommand.ExecuteAsync(null);
                break;
            case "Euterpe":
                await mainVm.NavigateToEuterpeDownloadCommand.ExecuteAsync(null);
                break;
            case "Sponsor":
                await mainVm.NavigateToSponsorCommand.ExecuteAsync(true);
                break;
        }
    }

    // 打开外部链接
    [RelayCommand]
    private void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 忽略打开失败的异常
        }
    }

    // 启动游戏
    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            await mainVm.LaunchGameCommand.ExecuteAsync(null);
        }
    }
}
