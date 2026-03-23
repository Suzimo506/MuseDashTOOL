using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Services;
using MdModManager.Helpers;
using ICSharpCode.SharpZipLib.Zip;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MdModManager.ViewModels;

/// <summary>
/// 设置页面的 ViewModel，管理全局配置
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _configService;

    // 可用的下载源列表
    [ObservableProperty]
    private string[] _downloadSources = new[] { "高速 DNS", "Suzimo", "kkgithub.com", "github.com" };

    // 控制主设置面板与颜色子面板的切换
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainPanelVisible))]
    private bool _isColorPanelVisible = false;

    public bool IsMainPanelVisible => !IsColorPanelVisible;

    // 可用透明效果列表
    [ObservableProperty]
    private string[] _transparencyModes = new[] { "无效果", "全透明", "Mica（云母）", "Acrylic（亚克力）" };

    // 可用字体列表
    public ObservableCollection<string> AvailableFonts { get; } = new ObservableCollection<string> { "（默认字体）" };

    private bool _isUpdatingFonts = false;

    private readonly INotificationService? _notificationService;
    
    // 撤回缓冲区，用于保存一键清除前的数据
    private string? _prevCustomThemeTextColor;
    private string? _prevRightPanelTextColor;
    private string? _prevCustomThemeColor;
    
    [ObservableProperty]
    private bool _canUndoReset = false;

    // ─────────────────────────────── 原有属性 ───────────────────────────────

    /// <summary>当前选定的下载源，与 ConfigService 同步</summary>
    public string SelectedDownloadSource
    {
        get
        {
            var val = _configService.Config.DownloadSource;
            if (_configService.Config.UseOptimizedDns && val == "suzimo.online") return "高速 DNS";
            if (val == "ghproxy.net") return "github.com";
            if (val == "suzimo.online") return "Suzimo";
            return val;
        }
        set
        {
            string newVal;
            bool useOpt = false;

            if (value == "高速 DNS")
            {
                newVal = "suzimo.online";
                useOpt = true;
            }
            else if (value == "Suzimo")
            {
                newVal = "suzimo.online";
                useOpt = false;
            }
            else
            {
                newVal = value;
                useOpt = false;
            }

            bool changed = false;
            if (_configService.Config.DownloadSource != newVal)
            {
                _configService.Config.DownloadSource = newVal;
                changed = true;
            }
            if (_configService.Config.UseOptimizedDns != useOpt)
            {
                _configService.Config.UseOptimizedDns = useOpt;
                HttpHelper.UseOptimizedIps = useOpt;
                OnPropertyChanged(nameof(UseOptimizedDns));
                changed = true;
            }

            if (changed)
            {
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    public SettingsViewModel(IConfigService configService, INotificationService? notificationService = null)
    {
        _configService = configService;
        _notificationService = notificationService ?? CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<INotificationService>();
        
        // 初始化静态帮助类的优选状态
        HttpHelper.UseOptimizedIps = _configService.Config.UseOptimizedDns;
    }

    /// <summary>永久关闭"下载不兼容 mod 确认弹窗"的开关</summary>
    public bool SuppressIncompatibleModWarning
    {
        get => _configService.Config.SuppressIncompatibleModWarning;
        set
        {
            if (_configService.Config.SuppressIncompatibleModWarning != value)
            {
                _configService.Config.SuppressIncompatibleModWarning = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>自动翻译 Mod 详情信息的开关</summary>
    public bool AutoTranslateDescriptions
    {
        get => _configService.Config.AutoTranslateDescriptions;
        set
        {
            if (_configService.Config.AutoTranslateDescriptions != value)
            {
                _configService.Config.AutoTranslateDescriptions = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>删除时不再显示确认弹窗</summary>
    public bool SuppressDeleteConfirmation
    {
        get => _configService.Config.SuppressDeleteConfirmation;
        set
        {
            if (_configService.Config.SuppressDeleteConfirmation != value)
            {
                _configService.Config.SuppressDeleteConfirmation = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>谱面名称过长时滚动显示</summary>
    public bool EnableChartNameMarquee
    {
        get => _configService.Config.EnableChartNameMarquee;
        set
        {
            if (_configService.Config.EnableChartNameMarquee != value)
            {
                _configService.Config.EnableChartNameMarquee = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>启动游戏时不再显示确认弹窗</summary>
    public bool SuppressLaunchGameConfirmation
    {
        get => _configService.Config.SuppressLaunchGameConfirmation;
        set
        {
            if (_configService.Config.SuppressLaunchGameConfirmation != value)
            {
                _configService.Config.SuppressLaunchGameConfirmation = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>以原版启动游戏</summary>
    public bool IsOriginalModeEnabled
    {
        get => _configService.Config.IsOriginalModeEnabled;
        set
        {
            if (_configService.Config.IsOriginalModeEnabled != value)
            {
                _configService.Config.IsOriginalModeEnabled = value;
                if (value)
                {
                    // 原版启动时，隐藏控制台选项应关闭 (因为原版根本没有控制台)
                    HideConsoleOnLaunch = false;
                }
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>启动游戏时隐藏控制台</summary>
    public bool HideConsoleOnLaunch
    {
        get => _configService.Config.HideConsoleOnLaunch;
        set
        {
            if (_configService.Config.HideConsoleOnLaunch != value)
            {
                _configService.Config.HideConsoleOnLaunch = value;
                if (value)
                {
                    // 开启隐藏控制台时，应确保“以原版启动”是关闭的
                    IsOriginalModeEnabled = false;
                }
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>谱面试听音量</summary>
    public double ChartPreviewVolume
    {
        get => _configService.Config.ChartPreviewVolume;
        set
        {
            if (Math.Abs(_configService.Config.ChartPreviewVolume - value) > 0.001)
            {
                _configService.Config.ChartPreviewVolume = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>是否解锁了隐藏的自定义背景功能</summary>
    public bool IsSecretBackgroundUnlocked
    {
        get => _configService.Config.IsSecretBackgroundUnlocked;
        set
        {
            if (_configService.Config.IsSecretBackgroundUnlocked != value)
            {
                _configService.Config.IsSecretBackgroundUnlocked = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>自定义背景图路径</summary>
    public string CustomBackgroundImagePath
    {
        get => _configService.Config.CustomBackgroundImagePath;
        set
        {
            if (_configService.Config.CustomBackgroundImagePath != value)
            {
                _configService.Config.CustomBackgroundImagePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasBackgroundImage));
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateBackground());
            }
        }
    }

    public bool HasBackgroundImage => !string.IsNullOrEmpty(CustomBackgroundImagePath);

    /// <summary>自定义背景图亮度 (0.1~1.0)</summary>
    public double CustomBackgroundOpacity
    {
        get => _configService.Config.CustomBackgroundOpacity;
        set
        {
            if (Math.Abs(_configService.Config.CustomBackgroundOpacity - value) > 0.001)
            {
                _configService.Config.CustomBackgroundOpacity = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateBackground());
            }
        }
    }

    /// <summary>隐藏模式下自定义的主题文字颜色</summary>
    public string CustomThemeTextColor
    {
        get => string.IsNullOrEmpty(_configService.Config.CustomThemeTextColor) ? "#" : _configService.Config.CustomThemeTextColor;
        set
        {
            var val = value ?? "";
            if (!val.StartsWith("#")) val = "#" + val;
            
            bool changed = _configService.Config.CustomThemeTextColor != val;
            bool neededPrefix = (value ?? "") != val;

            if (changed || neededPrefix)
            {
                _configService.Config.CustomThemeTextColor = val;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateThemeColors());
            }
        }
    }

    /// <summary>全局图标与控件主题色</summary>
    public string CustomThemeColor
    {
        get => string.IsNullOrEmpty(_configService.Config.CustomThemeColor) ? "#" : _configService.Config.CustomThemeColor;
        set
        {
            var val = value ?? "";
            if (!val.StartsWith("#")) val = "#" + val;
            
            bool changed = _configService.Config.CustomThemeColor != val;
            bool neededPrefix = (value ?? "") != val;

            if (changed || neededPrefix)
            {
                _configService.Config.CustomThemeColor = val;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateThemeColors());
            }
        }
    }

    /// <summary>右侧主面板文本颜色</summary>
    public string RightPanelTextColor
    {
        get => string.IsNullOrEmpty(_configService.Config.RightPanelTextColor) ? "#" : _configService.Config.RightPanelTextColor;
        set
        {
            var val = value ?? "";
            if (!val.StartsWith("#")) val = "#" + val;

            bool changed = _configService.Config.RightPanelTextColor != val;
            bool neededPrefix = (value ?? "") != val;

            if (changed || neededPrefix)
            {
                _configService.Config.RightPanelTextColor = val;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateThemeColors());
            }
        }
    }

    /// <summary>左侧按键字体横向间隔</summary>
    public double NavButtonLetterSpacing
    {
        get => _configService.Config.NavButtonLetterSpacing;
        set
        {
            if (Math.Abs(_configService.Config.NavButtonLetterSpacing - value) > 0.001)
            {
                _configService.Config.NavButtonLetterSpacing = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateNavButtonLetterSpacing());
            }
        }
    }

    /// <summary>背景模糊程度</summary>
    public double BackgroundBlurRadius
    {
        get => _configService.Config.BackgroundBlurRadius;
        set
        {
            if (Math.Abs(_configService.Config.BackgroundBlurRadius - value) > 0.001)
            {
                _configService.Config.BackgroundBlurRadius = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateBackgroundBlurRadius());
            }
        }
    }

    // ─────────────────────────────── 新增：颜色面板属性 ───────────────────────────────

    /// <summary>窗口透明效果 (None / AcrylicBlur / Transparent)</summary>
    public string WindowTransparencyMode
    {
        get => _configService.Config.WindowTransparencyMode;
        set
        {
            if (_configService.Config.WindowTransparencyMode != value)
            {
                _configService.Config.WindowTransparencyMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedTransparencyIndex));
                OnPropertyChanged(nameof(IsWindowOpacityEnabled));
                OnPropertyChanged(nameof(TransparencyModeDisplayName));
                // 切换到任何模式时重置透明度为 1.0（无模式使用滑条）
                WindowOpacity = 1.0;
                
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateTransparency());
            }
        }
    }

    /// <summary>透明效果 ComboBox 索引器 (0=None, 1=Transparent, 2=Mica, 3=AcrylicBlur)</summary>
    public int SelectedTransparencyIndex
    {
        get => WindowTransparencyMode switch
        {
            "Transparent" => 1,
            "Mica"        => 2,
            "AcrylicBlur" => 3,
            _             => 0
        };
        set
        {
            WindowTransparencyMode = value switch
            {
                1 => "Transparent",
                2 => "Mica",
                3 => "AcrylicBlur",
                _ => "None"
            };
        }
    }

    /// <summary>应用主题 (Dark / Light)</summary>
    public string AppTheme
    {
        get => _configService.Config.AppTheme;
        set
        {
            if (_configService.Config.AppTheme != value)
            {
                _configService.Config.AppTheme = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(ThemeToggleIconData));
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateThemeColors());
            }
        }
    }

    /// <summary>是否为浅色主题</summary>
    public bool IsLightTheme => AppTheme == "Light";

    /// <summary>主题切换按钮图标路径数据</summary>
    public string ThemeToggleIconData => IsLightTheme 
        ? "M524.72396 790.811881C364.001584 790.811881 233.238812 665.752079 233.238812 512c0-153.726733 130.762772-278.811881 291.485148-278.811881S816.209109 358.273267 816.209109 512c0 153.752079-130.762772 278.811881-291.485149 278.811881z m0-504.928317c-130.357228 0-236.407129 101.436832-236.407128 226.116436 0 124.70495 106.049901 226.116436 236.407128 226.116436s236.407129-101.411485 236.407129-226.091089c0-124.70495-106.049901-226.141782-236.407129-226.141783z M797.908911 622.485545Q821.278416 569.714059 821.278416 512q0-57.688713-23.369505-110.485545-22.558416-50.946535-63.670495-90.233663Q647.299802 228.118812 524.72396 228.118812t-209.514455 83.16198q-41.112079 39.287129-63.670495 90.233663-23.369505 52.796832-23.369505 110.510892 0 57.688713 23.369505 110.460198 22.558416 50.946535 63.670495 90.233663Q402.148119 795.881188 524.72396 795.881188t209.514456-83.136634q41.112079-39.337822 63.670495-90.259009zM727.242772 318.605941Q811.139802 398.853069 811.139802 512t-83.89703 193.394059q-83.998416 80.348515-202.518812 80.348515t-202.518811-80.348515Q238.308119 625.146931 238.308119 512t83.89703-193.394059Q406.203564 238.257426 524.72396 238.257426t202.518812 80.348515z m-31.936633 356.879208q70.894257-67.80198 70.894257-163.485149 0-95.683168-70.894257-163.485149-70.792871-67.700594-170.582179-67.700594-99.789307 0-170.582178 67.700594-70.894257 67.80198-70.894257 163.485149 0 95.683168 70.894257 163.485149 70.792871 67.700594 170.582178 67.700594 99.789307 0 170.582179-67.700594z m-6.995644-319.619802q67.751287 64.785743 67.751287 156.134653t-67.751287 156.134653q-67.852673 64.912475-163.586535 64.912476-95.733861 0-163.586534-64.887129-67.751287-64.811089-67.751287-156.134653 0-91.374257 67.751287-156.185347 67.852673-64.887129 163.586534-64.887129 95.733861 0 163.586535 64.887129z M537.397228 968.237624a25.346535 25.346535 0 0 1-25.346535-25.346535v-76.039604a25.346535 25.346535 0 1 1 50.693069 0v76.039604a25.346535 25.346535 0 0 1-25.346534 25.346535z M558.916436 964.38495q8.896634-8.896634 8.896633-21.493861v-76.039604q0-12.597228-8.92198-21.519208-8.871287-8.896634-21.493861-8.896633-12.597228 0-21.493862 8.896633-8.92198 8.92198-8.92198 21.519208v76.039604q0 12.597228 8.896634 21.493861 8.92198 8.92198 21.519208 8.921981t21.519208-8.921981zM557.674455 866.851485v76.039604q0 8.389703-5.931089 14.346139-5.956436 5.931089-14.346138 5.931089-8.389703 0-14.346139-5.931089Q517.12 951.255446 517.12 942.891089v-76.039604q0-8.41505 5.931089-14.346138 5.956436-5.931089 14.346139-5.93109 8.389703 0 14.346138 5.93109 5.931089 5.931089 5.931089 14.346138z M537.397228 182.49505a25.346535 25.346535 0 0 1-25.346535-25.346535V81.108911a25.346535 25.346535 0 0 1 50.693069 0v76.039604a25.346535 25.346535 0 0 1-25.346534 25.346535z M558.916436 178.667723Q567.813069 169.745743 567.813069 157.148515V81.108911q0-12.597228-8.896633-21.519208Q549.994455 50.693069 537.397228 50.693069t-21.519208 8.896634Q506.981386 68.511683 506.981386 81.108911v76.039604q0 12.597228 8.896634 21.519208 8.92198 8.896634 21.519208 8.896633t21.519208-8.896633zM557.674455 81.108911v76.039604q0 8.389703-5.931089 14.346138Q545.761584 177.425743 537.397228 177.425743q-8.389703 0-14.346139-5.93109Q517.12 165.512871 517.12 157.148515V81.108911q0-8.389703 5.931089-14.346139Q529.032871 60.831683 537.397228 60.831683q8.389703 0 14.346138 5.931089 5.931089 5.956436 5.931089 14.346139z M213.291089 841.50495c-7.857426 0-15.714851-2.407921-21.747327-7.198415-11.988911-9.631683-11.988911-25.194455 0-34.800792l65.241981-52.188515c12.014257-9.606337 31.480396-9.606337 43.494653 0 11.988911 9.631683 11.988911 25.194455 0 34.800792L235.013069 834.306535c-6.007129 4.815842-13.864554 7.198416-21.747326 7.198415z M213.291089 846.574257q14.523564 0 24.915644-8.313663l65.24198-52.188515q10.89901-8.719208 10.89901-21.341782t-10.89901-21.367129q-10.392079-8.313663-24.915644-8.313663t-24.915643 8.313663L188.375446 795.551683q-10.89901 8.719208-10.89901 21.341782t10.89901 21.367129q10.392079 8.313663 24.915643 8.313663z m83.82099-68.435643l-65.24198 52.213861q-7.60396 6.083168-18.57901 6.083169-10.949703 0-18.57901-6.083169-7.09703-5.70297-7.097029-13.433663 0-7.75604 7.097029-13.433663l65.24198-52.213862q7.60396-6.083168 18.57901-6.083168 10.949703 0 18.57901 6.083168 7.09703 5.70297 7.09703 13.433663 0 7.75604-7.09703 13.433664z M764.755644 283.881188a24.586139 24.586139 0 0 1-17.387723-41.999208l52.213861-52.188515a24.586139 24.586139 0 1 1 34.775446 34.800792l-52.213862 52.188515a24.535446 24.535446 0 0 1-17.362376 7.198416z M764.755644 288.950495q12.293069 0 20.98693-8.693861l52.213862-52.188515q8.668515-8.693861 8.668514-20.961584 0-12.293069-8.693861-20.986931-8.693861-8.693861-20.986931-8.693861-12.267723 0-20.961584 8.693861l-52.188515 52.188515q-8.693861 8.693861-8.693861 20.961584 0 12.293069 8.693861 20.986931 8.693861 8.693861 20.986931 8.693861z m13.839207-15.866931q-5.728317 5.728317-13.813861 5.728317-8.110891 0-13.813861-5.728317-5.728317-5.728317-5.728317-13.813861 0-8.085545 5.728317-13.788515l52.188515-52.213861q5.70297-5.70297 13.788514-5.702971 8.110891 0 13.813862 5.728317 5.728317 5.728317 5.728317 13.813862 0 8.085545-5.728317 13.788515l-52.163169 52.213861z M278.533069 283.881188c-7.857426 0-15.740198-2.407921-21.747326-7.198416l-65.241981-52.213861c-11.988911-9.58099-11.988911-25.143762 0-34.775446 12.014257-9.606337 31.480396-9.606337 43.494654 0l65.24198 52.213862c11.988911 9.58099 11.988911 25.143762 0 34.775445-6.007129 4.815842-13.889901 7.198416-21.747327 7.198416z M278.533069 288.950495q14.498218 0 24.890297-8.313663 10.924356-8.744554 10.924357-21.367129t-10.89901-21.341782L238.206733 185.739406q-10.392079-8.313663-24.915644-8.313663-14.498218 0-24.915643 8.313663-10.89901 8.744554-10.89901 21.367129t10.89901 21.341782l65.24198 52.188515q10.392079 8.313663 24.915643 8.313663z m25.67604-29.655445q0 7.730693-7.09703 13.433663-7.629307 6.083168-18.57901 6.083168t-18.57901-6.083168L194.712079 220.514851q-7.09703-5.677624-7.097029-13.433663 0-7.730693 7.097029-13.433663 7.629307-6.083168 18.57901-6.083169t18.57901 6.083169L297.112079 245.861386q7.09703 5.677624 7.09703 13.433664z M816.969505 841.50495a24.535446 24.535446 0 0 1-17.413069-7.198415l-52.188515-52.213862a24.586139 24.586139 0 1 1 34.800792-34.775445l52.188515 52.213861A24.586139 24.586139 0 0 1 816.944158 841.50495z M816.969505 846.574257q12.267723 0 20.961584-8.693861 8.693861-8.693861 8.693861-20.961584 0-12.293069-8.693861-20.986931l-52.188515-52.213861q-8.693861-8.668515-20.961584-8.668515-12.293069 0-20.986931 8.693861-8.693861 8.693861-8.693861 20.961584 0 12.293069 8.693861 20.986931l52.188515 52.213862q8.693861 8.668515 20.961584 8.668514z m19.516832-29.655445q0 8.085545-5.728317 13.788515-5.728317 5.728317-13.813862 5.728317-8.085545 0-13.788514-5.728317l-52.213862-52.188515q-5.70297-5.728317-5.70297-13.813862 0-8.085545 5.728317-13.788514 5.728317-5.728317 13.813861-5.728317 8.085545 0 13.813861 5.728317l52.163169 52.188514q5.728317 5.728317 5.728317 13.813862z M157.199208 537.346535H81.159604a25.346535 25.346535 0 0 1 0-50.69307h76.039604a25.346535 25.346535 0 0 1 0 50.69307z M178.718416 533.519208Q187.61505 524.597228 187.61505 512t-8.896634-21.519208Q169.796436 481.584158 157.199208 481.584158H81.159604q-12.597228 0-21.519208 8.896634-8.896634 8.92198-8.896634 21.519208t8.896634 21.519208q8.92198 8.896634 21.519208 8.896634h76.039604q12.597228 0 21.519208-8.896634z m-7.173069-35.865347q5.931089 5.956436 5.931089 14.346139 0 8.389703-5.931089 14.346139Q165.563564 532.277228 157.199208 532.277228H81.159604q-8.389703 0-14.346139-5.931089Q60.882376 520.364356 60.882376 512q0-8.389703 5.931089-14.346139Q72.795248 491.722772 81.159604 491.722772h76.039604q8.389703 0 14.346139 5.931089z M942.941782 537.346535h-76.039604a25.346535 25.346535 0 0 1 0-50.69307h76.039604a25.346535 25.346535 0 0 1 0 50.69307z M964.46099 533.519208q8.896634-8.92198 8.896634-21.519208t-8.896634-21.519208Q955.53901 481.584158 942.941782 481.584158h-76.039604q-12.597228 0-21.519208 8.896634-8.896634 8.92198-8.896633 21.519208t8.896633 21.519208q8.92198 8.896634 21.519208 8.896634h76.039604q12.597228 0 21.519208-8.896634z m-7.173069-35.865347q5.931089 5.956436 5.931089 14.346139 0 8.389703-5.931089 14.346139-5.956436 5.931089-14.346139 5.931089h-76.039604q-8.389703 0-14.346138-5.931089Q846.62495 520.364356 846.62495 512q0-8.389703 5.93109-14.346139 5.956436-5.931089 14.346138-5.931089h76.039604q8.389703 0 14.346139 5.931089z"
        : "M511.886222 79.644444a432.241778 432.241778 0 0 0-423.936 516.721778 432.355556 432.355556 0 0 0 589.368889 315.050667 432.298667 432.298667 0 0 0 266.865778-398.506667 133.916444 133.916444 0 0 0 0-14.108444 34.133333 34.133333 0 0 0-47.616-29.411556 247.808 247.808 0 0 1-101.319111 20.138667h-0.568889a252.131556 252.131556 0 0 1-228.238222-361.016889 34.133333 34.133333 0 0 0-30.776889-48.867556h-23.779556z m282.680889 478.151112c26.851556 0.341333 53.532444-2.730667 79.473778-9.102223a364.202667 364.202667 0 0 1-433.152 320.398223 363.975111 363.975111 0 0 1-265.272889-496.412445 364.088889 364.088889 0 0 1 310.385778-223.857778 320.568889 320.568889 0 0 0 308.565333 408.974223z";

    /// <summary>不透明度滑条仅在有背景图时可用（控制图片透明度）</summary>
    public bool IsWindowOpacityEnabled => HasBackgroundImage;

    /// <summary>当前透明效果的可读显示名称</summary>
    public string TransparencyModeDisplayName => WindowTransparencyMode switch
    {
        "Transparent" => "全透明",
        "Mica"        => "Mica（云母）",
        "AcrylicBlur" => "Acrylic（亚克力）",
        _             => "无效果"
    };

    /// <summary>窗口整体透明度</summary>
    public double WindowOpacity
    {
        get => _configService.Config.WindowOpacity;
        set
        {
            // 如果不可用，强制保持为 1.0
            if (!IsWindowOpacityEnabled) value = 1.0;

            if (Math.Abs(_configService.Config.WindowOpacity - value) > 0.001)
            {
                _configService.Config.WindowOpacity = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
                NotifyMainWindow(mwVm => mwVm.UpdateWindowOpacity());
            }
        }
    }

    /// <summary>自定义字体名称（可能并非 Avalonia 可直接用的 FamilyName）</summary>
    public string CustomFontFamily
    {
        get => _configService.Config.CustomFontFamily;
        set
        {
            if (_configService.Config.CustomFontFamily != value)
            {
                _configService.Config.CustomFontFamily = value ?? "";
                OnPropertyChanged();
                
                // 通知 MainWindow 更新字体
                NotifyMainWindow(mwVm => mwVm.UpdateFontFamily());
                _ = _configService.SaveAsync();
            }
        }
    }

    /// <summary>自定义字体大小</summary>
    public double CustomFontSize
    {
        get => _configService.Config.CustomFontSize;
        set
        {
            // Clamp value between 12 and 17
            double clampedValue = Math.Clamp(value, 12.0, 17.0);
            if (Math.Abs(_configService.Config.CustomFontSize - clampedValue) > 0.01)
            {
                _configService.Config.CustomFontSize = clampedValue;
                OnPropertyChanged();
                
                NotifyMainWindow(mwVm => mwVm.UpdateFontSize());
                _ = _configService.SaveAsync();
            }
        }
    }/// <summary>字体 ComboBox 索引器</summary>
    public int SelectedFontIndex
    {
        get
        {
            if (string.IsNullOrEmpty(CustomFontFamily)) return 0;
            var idx = AvailableFonts.IndexOf(CustomFontFamily);
            return idx < 0 ? 0 : idx;
        }
        set
        {
            if (_isUpdatingFonts) return;
            if (value >= 0 && value < AvailableFonts.Count)
            {
                CustomFontFamily = value == 0 ? "" : AvailableFonts[value];
            }
        }
    }


    // ─────────────────────────────── 初始化 ───────────────────────────────

    /// <summary>初始化设置项显示</summary>
    public void Initialize()
    {
        System.Console.WriteLine($"[SettingsViewModel] Initialize start. IsMainPanel:{IsMainPanelVisible}, IsColor:{IsColorPanelVisible}");
        OnPropertyChanged(nameof(IsColorPanelVisible));
        OnPropertyChanged(nameof(IsMainPanelVisible));
        OnPropertyChanged(nameof(SelectedDownloadSource));
        OnPropertyChanged(nameof(SuppressIncompatibleModWarning));
        OnPropertyChanged(nameof(CustomBackgroundImagePath));
        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(CustomBackgroundOpacity));
        OnPropertyChanged(nameof(WindowTransparencyMode));
        OnPropertyChanged(nameof(SelectedTransparencyIndex));
        OnPropertyChanged(nameof(AppTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeToggleIconData));
        OnPropertyChanged(nameof(WindowOpacity));
        OnPropertyChanged(nameof(CustomFontFamily));
        OnPropertyChanged(nameof(SelectedFontIndex));
        OnPropertyChanged(nameof(NavButtonLetterSpacing));
        OnPropertyChanged(nameof(BackgroundBlurRadius));
        OnPropertyChanged(nameof(IsOriginalModeEnabled));
        OnPropertyChanged(nameof(HideConsoleOnLaunch));
        
        // 从系统和本地目录加载字体列表
        _ = RefreshFontListAsync();
    }

    // ─────────────────────────────── 命令 ───────────────────────────────

    [RelayCommand]
    private void OpenColorPanel() => IsColorPanelVisible = true;

    [RelayCommand]
    private void CloseColorPanel() => IsColorPanelVisible = false;

    [RelayCommand]
    private void ToggleTheme() => AppTheme = IsLightTheme ? "Dark" : "Light";

    [RelayCommand]
    private void SetTransparencyMode(string mode)
    {
        // Transparent/Mica/AcrylicBlur 与背景图不兼容（BackgroundAlpha 可以与图片共存）
        if (mode is "Transparent" or "Mica" or "AcrylicBlur" && HasBackgroundImage)
        {
            _notificationService?.ShowInfo("导入图片后无法使用该效果");
            return;
        }
        WindowTransparencyMode = mode ?? "None";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SelectBackgroundImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var storageProvider = mainWindow.StorageProvider;
                var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "选择自定义背景图片",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Images")
                        {
                            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                        }
                    }
                });

                if (files.Count > 0)
                {
                    // 导入图片时自动关闭 Transparent/Mica/Acrylic（与背景图不兼容）
                    if (WindowTransparencyMode is "Transparent" or "Mica" or "AcrylicBlur")
                        WindowTransparencyMode = "None";
                    CustomBackgroundImagePath = files[0].Path.LocalPath;
                    
                    // 导入图片后滑条解锁，通知 UI 更新
                    OnPropertyChanged(nameof(HasBackgroundImage));
                    OnPropertyChanged(nameof(IsWindowOpacityEnabled));
                }
            }
        }
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        CustomBackgroundImagePath = string.Empty;
        
        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(IsWindowOpacityEnabled));
    }

    [RelayCommand]
    private void SetSolidColorBackground()
    {
        // 预留
    }


    [RelayCommand]
    private async System.Threading.Tasks.Task OpenAuthorHomepageAsync()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://space.bilibili.com/289883561?spm_id_from=333.1007.0.0",
                UseShellExecute = true
            });
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"无法打开作者主页: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ResetColors()
    {
        // 保存当前值以便撤回
        _prevCustomThemeTextColor = CustomThemeTextColor;
        _prevRightPanelTextColor = RightPanelTextColor;
        _prevCustomThemeColor = CustomThemeColor;
        CanUndoReset = true;

        CustomThemeTextColor = string.Empty;
        RightPanelTextColor = string.Empty;
        CustomThemeColor = string.Empty;
        
        // 通知配置保存
        _ = _configService.SaveAsync();
        
        // 通知 UI 更新，虽然属性 Setter 已经做了，但显式重置一下依赖项
        OnPropertyChanged(nameof(CustomThemeTextColor));
        OnPropertyChanged(nameof(RightPanelTextColor));
        OnPropertyChanged(nameof(CustomThemeColor));
        
        // 如果 MainWindowViewModel 有监听这些，可以调用通知
        NotifyMainWindow(mwVm => mwVm.UpdateThemeColors());
    }

    [RelayCommand]
    private void UndoColors()
    {
        if (!CanUndoReset) return;

        CustomThemeTextColor = _prevCustomThemeTextColor ?? string.Empty;
        RightPanelTextColor = _prevRightPanelTextColor ?? string.Empty;
        CustomThemeColor = _prevCustomThemeColor ?? string.Empty;

        CanUndoReset = false;

        _ = _configService.SaveAsync();
        
        OnPropertyChanged(nameof(CustomThemeTextColor));
        OnPropertyChanged(nameof(RightPanelTextColor));
        OnPropertyChanged(nameof(CustomThemeColor));

        NotifyMainWindow(mwVm => mwVm.UpdateThemeColors());
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenHexColorHelpAsync()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://blog.csdn.net/TommyXu8023/article/details/89279180",
                UseShellExecute = true
            });
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"无法打开十六进制色帮助页: {ex.Message}");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ImportFontAsync()
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 17134))
        {
            _notificationService?.ShowFailure("导入失败", "windows版本过低，导入失败");
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                var storageProvider = mainWindow.StorageProvider;
                var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "选择字体文件或压缩包",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Fonts & Archives")
                        {
                            Patterns = new[] { "*.ttf", "*.otf", "*.ttc", "*.zip" }
                        }
                    }
                });

                if (files.Count > 0)
                {
                    var filePaths = files.Select(f => f.Path.LocalPath).ToList();
                    await ImportFontsFromFilesAsync(filePaths);
                }
            }
        }
    }

    public async System.Threading.Tasks.Task ImportFontsFromFilesAsync(List<string> filePaths)
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 17134))
        {
            _notificationService?.ShowFailure("导入失败", "windows版本过低，导入失败");
            return;
        }

        bool hasImported = false;

        // 1. Prepare a persistent staging folder (NOT cleaned up before install)
        var stagingDir = Path.Combine(Path.GetTempPath(), "MdModManager_FontStage");
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        var fontFiles = new List<string>();

        await System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var path in filePaths)
            {
                if (!File.Exists(path)) continue;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".ttf" || ext == ".otf" || ext == ".ttc")
                {
                    var dest = Path.Combine(stagingDir, Path.GetFileName(path));
                    File.Copy(path, dest, overwrite: true);
                    fontFiles.Add(dest);
                }
                else if (ext == ".zip")
                {
                    using var fs = File.OpenRead(path);
                    using var zf = new ZipFile(fs);
                    foreach (ZipEntry zipEntry in zf)
                    {
                        if (!zipEntry.IsFile) continue;
                        var entryName = zipEntry.Name;
                        var entryExt = Path.GetExtension(entryName).ToLowerInvariant();
                        if (entryExt == ".ttf" || entryExt == ".otf" || entryExt == ".ttc")
                        {
                            var dest = Path.Combine(stagingDir, Path.GetFileName(entryName));
                            using var zipStream = zf.GetInputStream(zipEntry);
                            using var streamWriter = File.Create(dest);
                            zipStream.CopyTo(streamWriter);
                            fontFiles.Add(dest);
                        }
                    }
                }
            }
        });

        if (fontFiles.Count > 0)
        {
            int installedCount = 0;

            // Per-user font install: no admin/UAC needed on Windows 10 1803+
            // Destination: %LOCALAPPDATA%\Microsoft\Windows\Fonts
            // Registry:    HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts
            var userFontsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");
            Directory.CreateDirectory(userFontsDir);

            await System.Threading.Tasks.Task.Run(() =>
            {
                using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", writable: true);

                foreach (var fontFile in fontFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileName(fontFile);
                        string fontName = Path.GetFileNameWithoutExtension(fontFile);
                        string dest = Path.Combine(userFontsDir, fileName);

                        // Copy font to user fonts directory
                        File.Copy(fontFile, dest, overwrite: true);

                        // Register in HKCU (per-user, no admin needed)
                        if (regKey != null)
                        {
                            string regName = $"{fontName} (TrueType)";
                            regKey.SetValue(regName, dest, Microsoft.Win32.RegistryValueKind.String);
                        }

                        installedCount++;
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[Font] Failed to install {fontFile}: {ex.Message}");
                    }
                }
            });

            if (installedCount > 0)
                hasImported = true;
            else
                _notificationService?.ShowFailure("字体安装失败", "无法写入用户字体目录");
        }
        else
        {
            _notificationService?.ShowInfo("未找到受支持的字体文件");
        }

        // Cleanup staging dir AFTER all installs
        try
        {
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        }
        catch { }

        if (hasImported)
        {
            await RefreshFontListAsync();
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                _notificationService?.ShowSuccess($"成功安装了 {fontFiles.Count} 个字体！请在列表中查找。");
            });
            NotifyMainWindow(mwVm => mwVm.UpdateFontFamily());
        }
    }

    private async System.Threading.Tasks.Task RefreshFontListAsync()
    {
        var fonts = new HashSet<string> { "（默认字体）" };
        var excludedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Marlett", "Webdings", "Wingdings", "Wingdings 2", "Wingdings 3",
            "Symbol", "Segoe Fluent Icons", "Segoe MDL2 Assets", "HoloLens MDL2 Assets",
            "Bookshelf Symbol 7", "MS Reference Specialty", "MT Extra"
        };

        // 使用 Avalonia 内部的方法获取系统正确注册的实际字体家族名称（如 "微软雅黑" 而非 "msyh"）
        try
        {
            var systemFonts = Avalonia.Media.FontManager.Current.SystemFonts;
            foreach (var f in systemFonts)
            {
                if (!string.IsNullOrWhiteSpace(f.Name) && !excludedFonts.Contains(f.Name) && !f.Name.StartsWith("."))
                {
                    fonts.Add(f.Name);
                }
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Failed to get system fonts: {ex.Message}");
        }

        await System.Threading.Tasks.Task.CompletedTask; // no local folder scan needed, system fonts only

        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            _isUpdatingFonts = true;
            AvailableFonts.Clear();
            var sortedFonts = fonts.OrderBy(f => f == "（默认字体）" ? 0 : 1).ThenBy(f => f).ToList();
            foreach (var f in sortedFonts)
            {
                AvailableFonts.Add(f);
            }
            _isUpdatingFonts = false;
            OnPropertyChanged(nameof(SelectedFontIndex)); // Update Selection
            OnPropertyChanged(nameof(AvailableFonts)); // Update UI just in case
        });
    }

    public bool UseOptimizedDns
    {
        get => _configService.Config.UseOptimizedDns;
        set
        {
            if (_configService.Config.UseOptimizedDns != value)
            {
                _configService.Config.UseOptimizedDns = value;
                HttpHelper.UseOptimizedIps = value;
                OnPropertyChanged();
                _ = _configService.SaveAsync();
            }
        }
    }

    // ─────────────────────────────── 内部辅助 ───────────────────────────────

    private static void NotifyMainWindow(System.Action<MainWindowViewModel> action)
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow?.DataContext is MainWindowViewModel mwVm)
            action(mwVm);
    }
}
