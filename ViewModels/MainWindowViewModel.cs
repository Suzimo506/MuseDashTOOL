using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using MdModManager.Services;
using MdModManager.Models;

namespace MdModManager.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IConfigService? _configService;
    private readonly IGamePathService? _gamePathService;
    private readonly INotificationService? _notificationService;
    private readonly ModStagingService? _stagingService;
    private readonly INavigationService? _navigationService;
    private readonly IAnnouncementService? _announcementService;

    [ObservableProperty]
    private object? _currentPage;

    private CancellationTokenSource? _currentPageCts;

    [ObservableProperty]
    private string _gamePathStatus = "Checking...";

    /// <summary>绑定到左下角通知气泡列表</summary>
    public ObservableCollection<DownloadNotification> Notifications =>
        _notificationService?.Notifications ?? new ObservableCollection<DownloadNotification>();

    public string CustomBackgroundImagePath => _configService?.Config?.CustomBackgroundImagePath ?? string.Empty;
    public double CustomBackgroundOpacity => _configService?.Config?.CustomBackgroundOpacity ?? 0.2;
    
    /// <summary>1.0 减去亮度所得的黑暗度遮罩透明度</summary>
    [ObservableProperty]
    private double _customBackgroundDarkness = 0.8;

    /// <summary>当暂存目录有未安装 Mod 文件时为 true，用于侧栏显示 !!!</summary>
    public bool HasStagedMods => _stagingService?.HasPendingFiles ?? false;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _customBackgroundBitmap;

    [ObservableProperty]
    private bool _isNormalTheme = true;

    [ObservableProperty]
    private Avalonia.Media.IBrush _sidebarBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"));

    [ObservableProperty]
    private Avalonia.Media.IBrush _contentBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"));

    [ObservableProperty]
    private Avalonia.Media.IBrush _windowBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"));


    [ObservableProperty]
    private Avalonia.Media.IBrush _themeTextMainBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0E0E0"));

    [ObservableProperty]
    private Avalonia.Media.IBrush _themeTextSubBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0"));

    [ObservableProperty]
    private Avalonia.Media.IBrush _themeTextTertiaryBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#606060"));

    [ObservableProperty]
    private Avalonia.Media.IBrush _navButtonMainBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0E0E0"));

    [ObservableProperty]
    private Avalonia.Media.IBrush _navButtonSubBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0"));

    /// <summary>整个窗口的透明度（0.1~1.0）</summary>
    [ObservableProperty]
    private double _windowOpacityValue = 1.0;

    /// <summary>当前字体族</summary>
    [ObservableProperty]
    private Avalonia.Media.FontFamily _currentFontFamily = Avalonia.Media.FontFamily.Default;

    [ObservableProperty]
    private double _currentFontSize = 14.0;

    [ObservableProperty]
    private double _fontScale = 1.0;

    [ObservableProperty]
    private double _navButtonLetterSpacing = 0.0;

    [ObservableProperty]
    private bool _isChartDownloadMenuExpanded;

    [ObservableProperty]
    private double _backgroundBlurRadius = 0.0;

    [ObservableProperty]
    private bool _isAnnouncementVisible = false;

    [ObservableProperty]
    private NoticeInfo? _currentAnnouncement;

    private readonly System.Collections.Generic.Queue<NoticeInfo> _pendingAnnouncements = new();

    /// <summary>使用说明按钮是否显示红点提示</summary>
    [ObservableProperty]
    private bool _showTutorialBadge;

    public MainWindowViewModel()
    {
        // 供设计器使用
    }

    public MainWindowViewModel(
        IConfigService configService,
        IGamePathService gamePathService,
        INotificationService notificationService,
        ModStagingService stagingService,
        INavigationService navigationService,
        IAnnouncementService announcementService)
    {
        _configService = configService;
        _gamePathService = gamePathService;
        _notificationService = notificationService;
        _stagingService = stagingService;
        _navigationService = navigationService;
        _announcementService = announcementService;

        // 当 HasPendingFiles 变化时，通知 UI 更新 HasStagedMods
        _stagingService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModStagingService.HasPendingFiles))
                OnPropertyChanged(nameof(HasStagedMods));
        };

        if (_navigationService != null)
        {
            _navigationService.OnRequestConfigNavigation += async (filePath) =>
            {
                await NavigateToConfigWithFileAsync(filePath);
            };
        }
    }

    private async Task NavigateToConfigWithFileAsync(string filePath)
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ConfigManagerViewModel>();
        vm.PreSelectedFilePath = filePath;
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts!.Token);
    }

    public async Task InitializeAsync()
    {
        if (_configService == null || _gamePathService == null) return;

        if (_configService.Config.IsFirstLaunch)
        {
            CurrentPage = Ioc.Default.GetRequiredService<TutorialViewModel>();
            _configService.Config.IsFirstLaunch = false;
            await _configService.SaveAsync();
        }
        else
        {
            CurrentPage = Ioc.Default.GetRequiredService<ModManagerViewModel>();
        }

        if (string.IsNullOrEmpty(_configService.Config.GamePath) ||
            !_gamePathService.IsValidGamePath(_configService.Config.GamePath))
        {
            var detectedPath = _gamePathService.DetectGamePath();
            if (!string.IsNullOrEmpty(detectedPath))
            {
                _configService.Config.GamePath = detectedPath;
                await _configService.SaveAsync();
            }
        }

        UpdateGamePathStatus();
        CheckAndShowNotification();
        UpdateBackground();
        UpdateTransparency();
        UpdateWindowOpacity();
        // 从配置中初始化特效值
        NavButtonLetterSpacing = _configService.Config.NavButtonLetterSpacing;
        BackgroundBlurRadius = _configService.Config.BackgroundBlurRadius;

        // 异步尝试获取公告，但不阻塞主进程
        _ = TryShowAnnouncementAsync();

        // 初始化红点提示（当前版本新内容标记）
        InitializeBadges();

        UpdateFontFamily();
        UpdateFontSize();
        UpdateNavButtonLetterSpacing();
        UpdateBackgroundBlurRadius();
        UpdateThemeColors();

        _currentPageCts?.Cancel();
        _currentPageCts = new CancellationTokenSource();

        if (CurrentPage is MelonLoaderViewModel mlvm)
            await mlvm.InitializeAsync(_currentPageCts.Token);
        else if (CurrentPage is ModManagerViewModel mmvm)
            await mmvm.InitializeAsync(_currentPageCts.Token);
        else if (CurrentPage is ConfigManagerViewModel cmvm)
            await cmvm.InitializeAsync(_currentPageCts.Token);
        else if (CurrentPage is ChartManagerViewModel chvm)
            await chvm.InitializeAsync(_currentPageCts.Token);
        else if (CurrentPage is ChartUploadViewModel cuvm)
            await cuvm.InitializeAsync(_currentPageCts.Token);
        else if (CurrentPage is TutorialViewModel tvm)
        {
            // 逻辑已移动至 InitializeAsync 开始处
        }

        // 后台预加载谱面列表 (三种分类的第一页)
        _ = Ioc.Default.GetRequiredService<ChartDownloadViewModel>().PreloadAllSortsAsync();

    }

    private void UpdateGamePathStatus()
    {
        if (_configService != null && _gamePathService != null)
        {
            GamePathStatus = _gamePathService.IsValidGamePath(_configService.Config.GamePath)
                ? $"Game Path: {_configService.Config.GamePath}"
                : "Game Path: Not Set or Invalid";
        }
    }

    public void UpdateBackground()
    {
        OnPropertyChanged(nameof(CustomBackgroundOpacity));
        CustomBackgroundDarkness = 1.0 - (_configService?.Config.CustomBackgroundOpacity ?? 1.0);
        var path = CustomBackgroundImagePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            CustomBackgroundBitmap?.Dispose();
            CustomBackgroundBitmap = null;
            IsNormalTheme = true;
            UpdateThemeColors();
            return;
        }

        try
        {
            // 每次重新加载图片以防原图被修改
            CustomBackgroundBitmap?.Dispose();
            using var stream = System.IO.File.OpenRead(path);
            CustomBackgroundBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            IsNormalTheme = false;
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"无法加载背景图: {ex.Message}");
            CustomBackgroundBitmap = null;
            IsNormalTheme = true;
        }
        UpdateThemeColors();
    }

    public void UpdateThemeColors()
    {
        // 读取当前主题偏好
        bool isLightTheme = _configService?.Config?.AppTheme == "Light";

        // 处理自定义全局文本颜色
        var customColorHex = _configService?.Config?.CustomThemeTextColor;
        Avalonia.Media.Color parsedColor = default;
        bool hasCustomColor = !string.IsNullOrEmpty(customColorHex) && Avalonia.Media.Color.TryParse(customColorHex, out parsedColor);
        Avalonia.Media.SolidColorBrush? customBrush = hasCustomColor ? new Avalonia.Media.SolidColorBrush(parsedColor) : null;
        Avalonia.Media.Color subColor = hasCustomColor ? Avalonia.Media.Color.FromArgb(190, parsedColor.R, parsedColor.G, parsedColor.B) : default;
        Avalonia.Media.SolidColorBrush? customSubBrush = hasCustomColor ? new Avalonia.Media.SolidColorBrush(subColor) : null;

        // --- 处理主题强调色 (固定为默认粉色) ---
        // 这是为了让 Slider, CheckBox 等原生控件保持粉色基调，不受主题色改变影响
        var defaultAccentColor = Avalonia.Media.Color.Parse("#d4237a");
        var accentBrush = new Avalonia.Media.SolidColorBrush(defaultAccentColor);
        var accentHoverBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, defaultAccentColor.R, defaultAccentColor.G, defaultAccentColor.B));
        var accentPressedBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(200, defaultAccentColor.R, defaultAccentColor.G, defaultAccentColor.B));

        // --- 处理 SVG 图标画刷 (动态，受“主题颜色”控制) ---
        var svgColorHex = _configService?.Config?.CustomThemeColor;
        Avalonia.Media.Color parsedSvgColor;
        if (string.IsNullOrEmpty(svgColorHex) || !Avalonia.Media.Color.TryParse(svgColorHex, out parsedSvgColor))
        {
            parsedSvgColor = defaultAccentColor; // 默认粉色 fallback
        }
        var svgIconBrush = new Avalonia.Media.SolidColorBrush(parsedSvgColor);
        var svgIconHoverBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, parsedSvgColor.R, parsedSvgColor.G, parsedSvgColor.B));
        var svgIconPressedBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(200, parsedSvgColor.R, parsedSvgColor.G, parsedSvgColor.B));

        // --- 处理右侧面板文本颜色 ---
        var rightColorHex = _configService?.Config?.RightPanelTextColor;
        Avalonia.Media.Color rightParsedColor = default;
        bool hasRightColor = !string.IsNullOrEmpty(rightColorHex) && Avalonia.Media.Color.TryParse(rightColorHex, out rightParsedColor);
        
        Avalonia.Media.SolidColorBrush? rightMainBrush = null;
        Avalonia.Media.SolidColorBrush? rightSubBrush = null;
        Avalonia.Media.SolidColorBrush? rightTertiaryBrush = null;
        
        if (hasRightColor)
        {
            rightMainBrush = new Avalonia.Media.SolidColorBrush(rightParsedColor);
            rightSubBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(190, rightParsedColor.R, rightParsedColor.G, rightParsedColor.B));
            rightTertiaryBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(120, rightParsedColor.R, rightParsedColor.G, rightParsedColor.B));
        }

        if (IsNormalTheme)
        {
            if (isLightTheme)
            {
                // ── 浅色主题 ──
                WindowBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF"));
                SidebarBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EFEFEF"));
                ContentBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF"));
                ThemeTextMainBrush = rightMainBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#202020"));
                ThemeTextSubBrush = rightSubBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#505050"));
                ThemeTextTertiaryBrush = rightTertiaryBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#808080"));
            }
            else
            {
                // ── 深色主题 ──
                WindowBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"));
                SidebarBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"));
                ContentBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E"));
                ThemeTextMainBrush = rightMainBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0E0E0"));
                ThemeTextSubBrush = rightSubBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0"));
                ThemeTextTertiaryBrush = rightTertiaryBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#606060"));
            }
        }
        else
        {
            // 有背景图，或者启用了全透明/云母/亚克力效果时：
            WindowBackground = Avalonia.Media.Brushes.Transparent;
            SidebarBackground = Avalonia.Media.Brushes.Transparent;
            ContentBackground = Avalonia.Media.Brushes.Transparent;

            if (isLightTheme && CustomBackgroundBitmap == null)
            {
                ThemeTextMainBrush = rightMainBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#202020"));
                ThemeTextSubBrush = rightSubBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#505050"));
                ThemeTextTertiaryBrush = rightTertiaryBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#808080"));
            }
            else
            {
                ThemeTextMainBrush = rightMainBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0E0E0"));
                ThemeTextSubBrush = rightSubBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0"));
                ThemeTextTertiaryBrush = rightTertiaryBrush ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#606060"));
            }
        }

        NavButtonMainBrush = customBrush ?? (isLightTheme && CustomBackgroundBitmap == null ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#202020")) : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0E0E0")));
        NavButtonSubBrush = customSubBrush ?? (isLightTheme && CustomBackgroundBitmap == null ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#505050")) : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0")));

        Avalonia.Media.IBrush cardBg, cardHoverBg, modCardBg, controlBg, controlHoverBg, controlPressedBg;
        if (IsNormalTheme)
        {
            if (isLightTheme)
            {
                cardBg       = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DFDFDF"));
                cardHoverBg  = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D5D5D5"));
                modCardBg    = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E2E2E2"));
                controlBg    = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5E5E5"));
                controlHoverBg   = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DADADA"));
                controlPressedBg = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CFCFCF"));
            }
            else
            {
                cardBg       = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#242424"));
                cardHoverBg  = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2A2A"));
                modCardBg    = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D2D30"));
                controlBg    = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2a2a2a"));
                controlHoverBg   = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3a3a3a"));
                controlPressedBg = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4a4a4a"));
            }
        }
        else
        {
            // 完全透明背景 — 不分主题
            cardBg           = Avalonia.Media.Brushes.Transparent;
            cardHoverBg      = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1AFFFFFF"));
            modCardBg        = Avalonia.Media.Brushes.Transparent;
            
            // 独立于主题的透明设置面板背景画刷
            Avalonia.Application.Current?.Resources.Remove("SettingsCardBgBrush");
            Avalonia.Application.Current?.Resources.Add("SettingsCardBgBrush", Avalonia.Media.Brushes.Transparent);

            controlBg        = Avalonia.Media.Brushes.Transparent;
            controlHoverBg   = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1AFFFFFF"));
            controlPressedBg = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#33FFFFFF"));
        }

        if (Avalonia.Application.Current != null)
        {
            if (IsNormalTheme)
            {
                Avalonia.Application.Current.Resources.Remove("SettingsCardBgBrush");
                Avalonia.Application.Current.Resources.Add("SettingsCardBgBrush", 
                    isLightTheme ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EAEAEA")) 
                                 : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#18FFFFFF")));
            }
            Avalonia.Application.Current.Resources["ThemeTextMainBrush"] = ThemeTextMainBrush;
            Avalonia.Application.Current.Resources["ThemeTextSubBrush"]  = ThemeTextSubBrush;
            Avalonia.Application.Current.Resources["ThemeTextTertiaryBrush"] = ThemeTextTertiaryBrush;
            Avalonia.Application.Current.Resources["NavButtonMainBrush"] = NavButtonMainBrush;
            Avalonia.Application.Current.Resources["NavButtonSubBrush"]  = NavButtonSubBrush;
            Avalonia.Application.Current.Resources["CardBgBrush"]        = cardBg;
            Avalonia.Application.Current.Resources["CardHoverBgBrush"]   = cardHoverBg;
            Avalonia.Application.Current.Resources["ModCardBgBrush"]     = modCardBg;
            Avalonia.Application.Current.Resources["ControlBgBrush"]     = controlBg;
            Avalonia.Application.Current.Resources["ControlHoverBgBrush"]    = controlHoverBg;
            Avalonia.Application.Current.Resources["ControlPressedBgBrush"] = controlPressedBg;
            
            // 全局主题高亮色 (供打勾框、滑动条、进度条使用) - 现在固定为粉色
            Avalonia.Application.Current.Resources["ThemeAccentBrush"] = accentBrush;
            Avalonia.Application.Current.Resources["ThemeAccentBrushPointerOver"] = accentHoverBrush;
            Avalonia.Application.Current.Resources["ThemeAccentBrushPressed"] = accentPressedBrush;

            // 自定义 SVG 图标颜色 (由“主题颜色”控制)
            Avalonia.Application.Current.Resources["SvgIconBrush"] = svgIconBrush;
            Avalonia.Application.Current.Resources["SvgIconHoverBrush"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["SvgIconPressedBrush"] = svgIconPressedBrush;

            // 根据主题色亮度自动计算对比色（黑或白），用于按钮文字
            var luminance = 0.299 * parsedSvgColor.R + 0.587 * parsedSvgColor.G + 0.114 * parsedSvgColor.B;
            var contrastColor = luminance > 150
                ? Avalonia.Media.Color.Parse("#000000")
                : Avalonia.Media.Color.Parse("#FFFFFF");
            Avalonia.Application.Current.Resources["SvgIconContrastBrush"] = new Avalonia.Media.SolidColorBrush(contrastColor);

            // 让滑动条滑块也跟随主题色
            Avalonia.Application.Current.Resources["ScrollBarThumbFill"] = svgIconBrush;
            Avalonia.Application.Current.Resources["ScrollBarThumbFillPointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["ScrollBarThumbFillPressed"] = svgIconPressedBrush;

            // 让下拉菜单选中/悬停高亮色也跟随主题色
            Avalonia.Application.Current.Resources["ComboBoxItemBackgroundPointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["ComboBoxItemBackgroundSelected"] = svgIconBrush;
            Avalonia.Application.Current.Resources["ComboBoxItemBackgroundSelectedPointerOver"] = svgIconHoverBrush;

            // 让下拉菜单项的文本颜色跟随“右侧面板文本颜色”
            Avalonia.Application.Current.Resources["ComboBoxItemForeground"] = ThemeTextMainBrush;
            Avalonia.Application.Current.Resources["ComboBoxItemForegroundPointerOver"] = ThemeTextMainBrush;
            Avalonia.Application.Current.Resources["ComboBoxItemForegroundSelected"] = ThemeTextMainBrush;
            Avalonia.Application.Current.Resources["ComboBoxItemForegroundSelectedPointerOver"] = ThemeTextMainBrush;

            // 让拉条 (Slider) 也跟随主题色
            Avalonia.Application.Current.Resources["SliderTrackValueFill"] = svgIconBrush;
            Avalonia.Application.Current.Resources["SliderTrackValueFillPointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["SliderTrackValueFillPressed"] = svgIconPressedBrush;
            Avalonia.Application.Current.Resources["SliderThumbBackground"] = svgIconBrush;
            Avalonia.Application.Current.Resources["SliderThumbBackgroundPointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["SliderThumbBackgroundPressed"] = svgIconPressedBrush;

            // 让勾选框 (CheckBox) 也跟随主题色
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundFillChecked"] = svgIconBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundFillCheckedPressed"] = svgIconPressedBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundStrokeChecked"] = svgIconBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundStrokeCheckedPressed"] = svgIconPressedBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundFillIndeterminate"] = svgIconBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundFillIndeterminatePointerOver"] = svgIconHoverBrush;
            Avalonia.Application.Current.Resources["CheckBoxCheckBackgroundFillIndeterminatePressed"] = svgIconPressedBrush;
        }

        // 同步 Fluent 主题变体
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant =
                isLightTheme && IsNormalTheme
                    ? Avalonia.Styling.ThemeVariant.Light
                    : Avalonia.Styling.ThemeVariant.Dark;
        }
    }

    /// <summary>根据配置更新窗口透明效果</summary>
    public void UpdateTransparency()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow == null) return;

        var mode = _configService?.Config?.WindowTransparencyMode ?? "None";
        desktop.MainWindow.TransparencyLevelHint = mode switch
        {
            "Mica"            => new[]
            {
                Avalonia.Controls.WindowTransparencyLevel.Mica,
                Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur,
                Avalonia.Controls.WindowTransparencyLevel.Transparent
            },
            "AcrylicBlur"     => new[]
            {
                Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur,
                Avalonia.Controls.WindowTransparencyLevel.Transparent
            },
            "Transparent"     => new[] { Avalonia.Controls.WindowTransparencyLevel.Transparent },
            _                 => new[] { Avalonia.Controls.WindowTransparencyLevel.None }
        };

        // 所有非 None 模式都需要透明背景
        bool needsTransparentBg = mode != "None";
        if (needsTransparentBg && IsNormalTheme)
        {
            IsNormalTheme = false;
            UpdateThemeColors();
        }
        else if (!needsTransparentBg && CustomBackgroundBitmap == null)
        {
            IsNormalTheme = true;
            UpdateThemeColors();
        }
        UpdateWindowOpacity();
    }

    /// <summary>更新图片不透明度 — 当有背景图时，滑动条控制图片的 Opacity；否则保持 1.0</summary>
    public void UpdateWindowOpacity()
    {
        if (CustomBackgroundBitmap != null)
        {
            WindowOpacityValue = _configService?.Config?.WindowOpacity ?? 1.0;
        }
        else
        {
            WindowOpacityValue = 1.0;
        }
    }

    /// <summary>更新全局字体</summary>
    public void UpdateFontFamily()
    {
        var font = _configService?.Config?.CustomFontFamily ?? "";
        if (string.IsNullOrWhiteSpace(font) || font == "（默认字体）" || font == "默认(系统字体)")
        {
            CurrentFontFamily = Avalonia.Media.FontFamily.Default;
        }
        else
        {
            try
            {
                // 在 Avalonia 中，可以通过解析名称来设置自定义字体族。
                // 如果是本地字体，可能需要特定的字体族表示形式，如 "resm:x?assembly#Family" 或文件路径格式。
                // But if they are just installed or we construct a FontFamily with a fallback, let's see.
                // For a local font file, we can map to its file path + family if we know it.
                // Actually Avalonia supports setting via family name if the system knows it.
                // Wait, if it's placed in the Local Fonts folder, Avalonia won't automatically pick it up just by family name unless it's registered in AppBuilder.
                // Since Avalonia 11 supports setting FontFamily to a file URI like "avares://YourAssembly/Assets/Fonts#MyFont", it's complex for dynamic files.
                // However, on Windows, if it's just copied to the `Fonts` folder, Avalonia's DirectWrite doesn't scan random folders. 
                // Let's use `avares` if we needed, but since we are dynamic, we can construct the pack URI or file URI:
                // `new FontFamily("file:///C:/Path/To/Fonts/#FamilyName")`
                
                CurrentFontFamily = Avalonia.Media.FontFamily.Parse(font);
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"无法加载字体: {ex.Message}");
                CurrentFontFamily = Avalonia.Media.FontFamily.Default; // 出错时回退到默认
            }
        }
    }

    /// <summary>更新全局字体大小</summary>
    public void UpdateFontSize()
    {
        CurrentFontSize = _configService?.Config?.CustomFontSize ?? 14.0;
        FontScale = CurrentFontSize / 14.0;
    }

    public void UpdateNavButtonLetterSpacing()
    {
        NavButtonLetterSpacing = _configService?.Config?.NavButtonLetterSpacing ?? 0.0;
    }

    public void UpdateBackgroundBlurRadius()
    {
        BackgroundBlurRadius = _configService?.Config?.BackgroundBlurRadius ?? 0.0;
    }

    private void CheckAndShowNotification()
    {
        if (_notificationService == null || _configService == null || _gamePathService == null) return;
        
        _notificationService.ClearPersistentNotifications();

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath) || !_gamePathService.IsValidGamePath(gamePath))
        {
            _notificationService.ShowInfo("未检测到游戏，请手动选择路径", -1);
        }
        else
        {
            try
            {
                var steamNameRaw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("LastGameNameUsed") as string;
                if (!string.IsNullOrEmpty(steamNameRaw))
                {
                    var bytes = System.Text.Encoding.GetEncoding(0).GetBytes(steamNameRaw);
                    var steamNameStr = System.Text.Encoding.UTF8.GetString(bytes);

                    _notificationService.ShowInfo($"欢迎回来\n{steamNameStr}", 1500);
                }
                else
                {
                    _notificationService.ShowInfo("欢迎回来", 1500);
                }
            }
            catch
            {
                // 如果注册表访问失败，回退到默认
                _notificationService.ShowInfo("欢迎回来", 1500);
            }
        }
    }

    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (_configService == null) return;
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath) || (_gamePathService != null && !_gamePathService.IsValidGamePath(gamePath)))
        {
            return;
        }

        var exePath = System.IO.Path.Combine(gamePath, "MuseDash.exe");
        if (!System.IO.File.Exists(exePath)) return;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var confirm = await MdModManager.Views.LaunchGameConfirmDialog.ShowDialogAsync(mainWindow, _configService);
                if (!confirm) return;
            }
        }

        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("MuseDash");
            bool killedAny = false;
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    killedAny = true;
                }
            }
            if (killedAny)
            {
                await Task.Delay(1500); // 给进程留出关闭的时间
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Error killing process: {ex.Message}");
        }

        try
        {
            // 如果是在 Steam 目录下，直接通过 Steam 协议启动，可以避免游戏被 Steam 的 DRM 强制关闭并重新拉起
            if (gamePath.Contains("steamapps", System.StringComparison.OrdinalIgnoreCase))
            {
                string steamUrl = "steam://rungameid/774171";
                if (_configService.Config.IsOriginalModeEnabled)
                {
                    steamUrl += "//--no-mods";
                }
                else if (_configService.Config.HideConsoleOnLaunch)
                {
                    steamUrl += "//--melonloader.hideconsole";
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = steamUrl,
                    UseShellExecute = true
                });
            }
            else
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = gamePath,
                    UseShellExecute = true
                };

                if (_configService.Config.IsOriginalModeEnabled)
                {
                    startInfo.Arguments = "--no-mods";
                }
                else if (_configService.Config.HideConsoleOnLaunch)
                {
                    startInfo.Arguments = "--melonloader.hideconsole";
                }

                System.Diagnostics.Process.Start(startInfo);
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Error starting process: {ex.Message}");
        }

        // 按钮冷却保护
        await Task.Delay(3000);
    }

    [RelayCommand]
    private async Task NavigateToSettingsAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<SettingsViewModel>();
        CurrentPage = vm;
        vm.Initialize();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task NavigateToAccountAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<AccountViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync();
    }

    [RelayCommand]
    private async Task NavigateToMelonLoaderAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<MelonLoaderViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts!.Token);
    }

    private static bool IsAlbumCollectionSectionPage(object? page)
    {
        return page is AlbumCollectionViewModel
            or AlbumDetailViewModel
            or CommunityCategoryDetailViewModel;
    }

    public void CleanupCurrentPage()
    {
        // 如果当前在高级设置中且有未确认的自定义 IP，阻止页面切换
        if (IsNavigationBlocked()) return;

        Ioc.Default.GetRequiredService<ChartDownloadViewModel>().StopPlayback();
        var isLeavingAlbumCollectionSection = IsAlbumCollectionSectionPage(CurrentPage);
        if (isLeavingAlbumCollectionSection)
        {
            Ioc.Default.GetRequiredService<AlbumCollectionViewModel>().ReleaseResources();
            ViewLocator.ClearCache(); // 彻底清理视图单例，释放内存
        }
        _currentPageCts?.Cancel();
        _currentPageCts = new CancellationTokenSource();
        
        if (CurrentPage is ChartManagerViewModel chartVm)
            chartVm.Dispose();
        else if (CurrentPage is AccountViewModel accountVm)
            accountVm.Cleanup(); // 离开账号页时释放多余记录，节省内存
    }

    /// <summary>检查是否有未确认的高级设置阻止导航</summary>
    private bool IsNavigationBlocked()
    {
        if (CurrentPage is SettingsViewModel settingsVm && settingsVm.IsAdvancedPanelVisible && settingsVm.HasUnconfirmedCustomIp())
        {
            _notificationService?.ShowFailure("无法离开", "请输入 IP 并点击确认后退出");
            return true;
        }
        return false;
    }

    [RelayCommand]
    private async Task NavigateToModManagerAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ModManagerViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts!.Token);
    }

    [RelayCommand]
    private async Task NavigateToConfigManagerAsync()
    {
        CleanupCurrentPage();

        System.Console.WriteLine("[调试] 正在导航至配置管理器...");
        var vm = Ioc.Default.GetRequiredService<ConfigManagerViewModel>();
        System.Console.WriteLine("[调试] ConfigManagerViewModel 已解析。");
        CurrentPage = vm;
        System.Console.WriteLine("[调试] CurrentPage 已更新。");
        await vm.InitializeAsync(_currentPageCts!.Token);
        System.Console.WriteLine("[调试] ConfigManagerViewModel InitializeAsync 已完成。");
    }

    [RelayCommand]
    private async Task NavigateToChartManagerAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ChartManagerViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts!.Token);
    }

    [RelayCommand]
    private async Task NavigateToChartDownloadAsync()
    {
        IsChartDownloadMenuExpanded = true;
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ChartDownloadViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts!.Token);
    }

    [RelayCommand]
    private async Task NavigateToChartUploadAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ChartUploadViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts!.Token);
    }

    [RelayCommand]
    public async Task NavigateToTutorialAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<TutorialViewModel>();
        CurrentPage = vm;

        // 点击后清除红点提示
        if (ShowTutorialBadge)
        {
            await DismissBadgeAsync("Tutorial");
            ShowTutorialBadge = false;
        }
    }

    [RelayCommand]
    private async Task NavigateToDownloadManagerAsync()
    {
        var prevPageTypeName = CurrentPage?.GetType().Name;
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<DownloadManagerViewModel>();
        vm.PreviousPageType = prevPageTypeName;
        vm.OnRequestBack += () => 
        {
            // 根据来源页面类型，调用对应的导航方法重新加载
            switch (vm.PreviousPageType)
            {
                case nameof(AlbumCollectionViewModel):
                    _ = NavigateToAlbumCollectionAsync();
                    break;
                case nameof(ChartDownloadViewModel):
                    _ = NavigateToChartDownloadAsync();
                    break;
                default:
                    // 兜底：回到谱面下载
                    _ = NavigateToChartDownloadAsync();
                    break;
            }
        };
        CurrentPage = vm;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task NavigateToAlbumCollectionAsync()
    {
        IsChartDownloadMenuExpanded = true;
        CleanupCurrentPage();


        var vm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync();
    }

    [RelayCommand]
    private void ToggleChartDownloadMenu()
    {
        IsChartDownloadMenuExpanded = !IsChartDownloadMenuExpanded;
    }

    [RelayCommand]
    private async Task GenerateLogAsync()
    {
        try
        {
            var runtimeDesktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            var exportRuntimeLogPath = System.IO.Path.Combine(runtimeDesktopPath, "MuseDashTOOL_runtime-debug.log");
            var runtimeLogPath = RuntimeLog.LogPath;
            RuntimeLog.Write("MainWindowViewModel", $"用户导出运行日志到桌面：{exportRuntimeLogPath}");

            // 左键标题时优先导出运行日志到桌面。
            // 运行日志会保留真实的请求、下载、更新和异常时序，比摘要日志更适合排查问题。
            RuntimeLog.Write("MainWindowViewModel", $"用户导出运行日志到桌面：{exportRuntimeLogPath}");

            if (!System.IO.File.Exists(runtimeLogPath))
            {
                await System.IO.File.WriteAllTextAsync(
                    exportRuntimeLogPath,
                    $"未找到运行日志文件。{System.Environment.NewLine}Expected: {runtimeLogPath}{System.Environment.NewLine}");
            }
            else
            {
                System.IO.File.Copy(runtimeLogPath, exportRuntimeLogPath, overwrite: true);
            }

            if (_notificationService != null)
            {
                _notificationService.ShowSuccess("运行日志已保存到桌面！");
            }

    }
        catch (System.Exception ex)
        {
            if (_notificationService != null)
            {
                _notificationService.ShowFailure("生成日志失败", ex.Message);
            }
        }
    }

    private async Task TryShowAnnouncementAsync()
    {
        if (_announcementService == null || _configService == null) return;

        var notices = await _announcementService.GetLatestAnnouncementsAsync();
        if (notices != null)
        {
            foreach (var notice in notices)
            {
                if (notice != null && notice.IsEnabled && !_configService.Config.SuppressedAnnouncements.Contains(notice.Id))
                {
                    _pendingAnnouncements.Enqueue(notice);
                }
            }
            ShowNextAnnouncement();
        }
    }

    private void ShowNextAnnouncement()
    {
        if (_pendingAnnouncements.Count > 0)
        {
            CurrentAnnouncement = _pendingAnnouncements.Dequeue();
            IsAnnouncementVisible = true;
        }
        else
        {
            IsAnnouncementVisible = false;
        }
    }

    [RelayCommand]
    private void CloseAnnouncement()
    {
        ShowNextAnnouncement();
    }

    [RelayCommand]
    private async Task SuppressAndCloseAnnouncementAsync()
    {
        if (CurrentAnnouncement != null && _configService != null)
        {
            if (!_configService.Config.SuppressedAnnouncements.Contains(CurrentAnnouncement.Id))
            {
                _configService.Config.SuppressedAnnouncements.Add(CurrentAnnouncement.Id);
                await _configService.SaveAsync();
            }
        }
        ShowNextAnnouncement();
    }

    // ──────────────────────────────────────────────────────────
    //  可复用的红点提示系统
    //  使用方法：
    //  1. 在 AppConfig.DismissedBadges 中存储 "版本号:标记名"
    //  2. 添加 [ObservableProperty] bool _showXxxBadge
    //  3. 在 InitializeBadges() 中调用 ShouldShowBadge("标记名")
    //  4. 在对应的 Navigate 方法中调用 DismissBadgeAsync("标记名")
    // ──────────────────────────────────────────────────────────

    /// <summary>当前程序版本号（与 UpdateService.CurrentVersion 保持一致）</summary>
    private const string CurrentAppVersion = "1.3.2";

    /// <summary>初始化所有红点提示状态</summary>
    private void InitializeBadges()
    {
        // v1.3.2: 使用说明页本版本不再显示红点
        ShowTutorialBadge = false;

        // 未来版本可以在这里继续添加，例如:
        // ShowSettingsBadge = ShouldShowBadge("Settings");
    }

    /// <summary>判断指定标记在当前版本是否应该显示红点</summary>
    private bool ShouldShowBadge(string badgeName)
    {
        if (_configService == null) return false;
        var key = $"{CurrentAppVersion}:{badgeName}";
        return !_configService.Config.DismissedBadges.Contains(key);
    }

    /// <summary>标记指定红点为已读，并持久化到配置文件</summary>
    private async Task DismissBadgeAsync(string badgeName)
    {
        if (_configService == null) return;
        var key = $"{CurrentAppVersion}:{badgeName}";
        if (!_configService.Config.DismissedBadges.Contains(key))
        {
            _configService.Config.DismissedBadges.Add(key);
            await _configService.SaveAsync();
        }
    }
}
