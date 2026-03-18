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
    private double _backgroundBlurRadius = 0.0;

    [ObservableProperty]
    private bool _isAnnouncementVisible = false;

    [ObservableProperty]
    private NoticeInfo? _currentAnnouncement;

    public MainWindowViewModel()
    {
        // For designer
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
        await vm.InitializeAsync(_currentPageCts.Token);
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
        else if (CurrentPage is TutorialViewModel tvm)
        {
            // Logic moved to InitializeAsync start
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
        CustomBackgroundDarkness = 1.0 - _configService.Config.CustomBackgroundOpacity;
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

        // Handle custom global text color
        var customColorHex = _configService?.Config?.CustomThemeTextColor;
        Avalonia.Media.Color parsedColor = default;
        bool hasCustomColor = !string.IsNullOrEmpty(customColorHex) && Avalonia.Media.Color.TryParse(customColorHex, out parsedColor);
        Avalonia.Media.SolidColorBrush? customBrush = hasCustomColor ? new Avalonia.Media.SolidColorBrush(parsedColor) : null;
        Avalonia.Media.Color subColor = hasCustomColor ? Avalonia.Media.Color.FromArgb(190, parsedColor.R, parsedColor.G, parsedColor.B) : default;
        Avalonia.Media.SolidColorBrush? customSubBrush = hasCustomColor ? new Avalonia.Media.SolidColorBrush(subColor) : null;

        // --- Handle ThemeAccentBrush (Fixed to default pink) ---
        // 这是为了让 Slider, CheckBox 等原生控件保持粉色基调，不受主题色改变影响
        var defaultAccentColor = Avalonia.Media.Color.Parse("#d4237a");
        var accentBrush = new Avalonia.Media.SolidColorBrush(defaultAccentColor);
        var accentHoverBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, defaultAccentColor.R, defaultAccentColor.G, defaultAccentColor.B));
        var accentPressedBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(200, defaultAccentColor.R, defaultAccentColor.G, defaultAccentColor.B));

        // --- Handle SvgIconBrush (Dynamic, controlled by "Theme Color") ---
        var svgColorHex = _configService?.Config?.CustomThemeColor;
        Avalonia.Media.Color parsedSvgColor;
        if (string.IsNullOrEmpty(svgColorHex) || !Avalonia.Media.Color.TryParse(svgColorHex, out parsedSvgColor))
        {
            parsedSvgColor = defaultAccentColor; // 默认粉色 fallback
        }
        var svgIconBrush = new Avalonia.Media.SolidColorBrush(parsedSvgColor);
        var svgIconHoverBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, parsedSvgColor.R, parsedSvgColor.G, parsedSvgColor.B));
        var svgIconPressedBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(200, parsedSvgColor.R, parsedSvgColor.G, parsedSvgColor.B));

        // --- Handle right panel text color ---
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
                // In Avalonia, setting a custom font family by name can be done by parsing the font name.
                // If the font is local, we might need a custom font family representation like "resm:x?assembly#Family" or a file path format.
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
                CurrentFontFamily = Avalonia.Media.FontFamily.Default; // Fallback to default on error
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
                // Fallback if registry access fails
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
        await vm.InitializeAsync(_currentPageCts.Token);
    }

    private void CleanupCurrentPage()
    {
        _currentPageCts?.Cancel();
        _currentPageCts = new CancellationTokenSource();
        
        if (CurrentPage is ChartManagerViewModel chartVm)
            chartVm.Dispose();
        else if (CurrentPage is ChartDownloadViewModel chartDownloadVm)
            chartDownloadVm.Dispose(); // 切换时将停止正在播放的试听音频
        else if (CurrentPage is AccountViewModel accountVm)
            accountVm.Cleanup(); // 离开账号页时释放多余记录，节省内存
    }

    [RelayCommand]
    private async Task NavigateToModManagerAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ModManagerViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts.Token);
    }

    [RelayCommand]
    private async Task NavigateToConfigManagerAsync()
    {
        CleanupCurrentPage();

        System.Console.WriteLine("[DEBUG] Navigating to Config Manager...");
        var vm = Ioc.Default.GetRequiredService<ConfigManagerViewModel>();
        System.Console.WriteLine("[DEBUG] ConfigManagerViewModel resolved.");
        CurrentPage = vm;
        System.Console.WriteLine("[DEBUG] CurrentPage updated.");
        await vm.InitializeAsync(_currentPageCts.Token);
        System.Console.WriteLine("[DEBUG] ConfigManagerViewModel InitializeAsync finished.");
    }

    [RelayCommand]
    private async Task NavigateToChartManagerAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ChartManagerViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts.Token);
    }

    [RelayCommand]
    private async Task NavigateToChartDownloadAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<ChartDownloadViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync(_currentPageCts.Token);
    }

    [RelayCommand]
    public async Task NavigateToTutorialAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<TutorialViewModel>();
        CurrentPage = vm;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task NavigateToDownloadManagerAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<DownloadManagerViewModel>();
        CurrentPage = vm;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task NavigateToAlbumCollectionAsync()
    {
        CleanupCurrentPage();

        var vm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
        CurrentPage = vm;
        await vm.InitializeAsync();
    }

    [RelayCommand]
    private async Task GenerateLogAsync()
    {
        try
        {
            var desktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            var logPath = System.IO.Path.Combine(desktopPath, "MuseDashTOOL_log.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== MuseDashTOOL Diagnostic Log ===");
            sb.AppendLine($"Timestamp: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS Version: {System.Environment.OSVersion}");
            sb.AppendLine($"64-bit OS: {System.Environment.Is64BitOperatingSystem}");
            sb.AppendLine($".NET Version: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            
            if (_configService != null)
            {
                var cfg = _configService.Config;
                sb.AppendLine();
                sb.AppendLine("--- Configuration & Preferences ---");
                sb.AppendLine($"GamePath: {cfg.GamePath}");
                sb.AppendLine($"DownloadSource: {cfg.DownloadSource}");
                sb.AppendLine($"OriginalModeEnabled: {cfg.IsOriginalModeEnabled}");
                
                sb.AppendLine();
                sb.AppendLine("--- UI & Aesthetics ---");
                sb.AppendLine($"Theme: {cfg.AppTheme}");
                sb.AppendLine($"TransparencyMode: {cfg.WindowTransparencyMode}");
                sb.AppendLine($"WindowOpacity: {cfg.WindowOpacity}");
                sb.AppendLine($"BackgroundBlur: {cfg.BackgroundBlurRadius}");
                sb.AppendLine($"CustomFont: {cfg.CustomFontFamily ?? "Default"} (Size: {cfg.CustomFontSize})");
                sb.AppendLine($"ThemeColor: {cfg.CustomThemeColor}");
                sb.AppendLine($"TextLinearSpacing: {cfg.NavButtonLetterSpacing}");

                sb.AppendLine();
                sb.AppendLine("--- Announcement System ---");
                sb.AppendLine($"Suppressed IDs ({cfg.SuppressedAnnouncements.Count}): {string.Join(", ", cfg.SuppressedAnnouncements)}");
            }
            
            if (_gamePathService != null && _configService != null)
            {
                sb.AppendLine();
                sb.AppendLine("--- Game & Mod State ---");
                sb.AppendLine($"Game Path Valid: {_gamePathService.IsValidGamePath(_configService.Config.GamePath)}");
                
                var mlDir = System.IO.Path.Combine(_configService.Config.GamePath, "MelonLoader");
                var modsDir = System.IO.Path.Combine(_configService.Config.GamePath, "Mods");
                
                sb.AppendLine($"MelonLoader Folder: {(System.IO.Directory.Exists(mlDir) ? "Present" : "Missing")}");
                sb.AppendLine($"Mods Folder: {(System.IO.Directory.Exists(modsDir) ? "Present" : "Missing")}");
                
                if (System.IO.Directory.Exists(modsDir))
                {
                    var dllFiles = System.IO.Directory.GetFiles(modsDir, "*.dll");
                    sb.AppendLine($"Installed DLL Mods ({dllFiles.Length}):");
                    foreach (var dll in dllFiles)
                    {
                        try
                        {
                            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion;
                            sb.AppendLine($" - {System.IO.Path.GetFileName(dll)} (v{version})");
                        }
                        catch { sb.AppendLine($" - {System.IO.Path.GetFileName(dll)} (Version unknown)"); }
                    }
                }
            }

            await System.IO.File.WriteAllTextAsync(logPath, sb.ToString());
            
            // 可以选择弹个通知告诉用户已生成，利用现有的 NotificationService
            if (_notificationService != null)
            {
                _notificationService.ShowSuccess("诊断日志已保存到桌面！");
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

        var notice = await _announcementService.GetLatestAnnouncementAsync();
        if (notice != null && notice.IsEnabled)
        {
            // 检查该 ID 是否已被屏蔽
            if (!_configService.Config.SuppressedAnnouncements.Contains(notice.Id))
            {
                CurrentAnnouncement = notice;
                IsAnnouncementVisible = true;
            }
        }
    }

    [RelayCommand]
    private void CloseAnnouncement()
    {
        IsAnnouncementVisible = false;
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
        IsAnnouncementVisible = false;
    }
}
