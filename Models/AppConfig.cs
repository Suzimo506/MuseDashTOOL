using System.Text.Json.Serialization;

namespace MdModManager.Models;

public class AppConfig
{
    public string GamePath { get; set; } = "";

    public string DownloadSource { get; set; } = "suzimo";

    /// <summary>启用高速 DNS 优选 (Cloudflare 节点竞速)</summary>
    public bool UseOptimizedDns { get; set; } = true;

    /// <summary>永久关闭"下载不兼容 mod"的二次确认弹窗</summary>
    public bool SuppressIncompatibleModWarning { get; set; } = false;

    /// <summary>自动翻译 Mod 详情信息为中文</summary>
    public bool AutoTranslateDescriptions { get; set; } = false;

    /// <summary>删除时不再显示确认弹窗</summary>
    public bool SuppressDeleteConfirmation { get; set; } = false;

    /// <summary>谱面名称过长时滚动显示</summary>
    public bool EnableChartNameMarquee { get; set; } = true;

    /// <summary>启动游戏时不再显示确认弹窗</summary>
    public bool SuppressLaunchGameConfirmation { get; set; } = false;


    /// <summary>谱面试听音量</summary>
    public double ChartPreviewVolume { get; set; } = 0.5;

    /// <summary>是否解锁了隐藏的自定义背景功能</summary>
    public bool IsSecretBackgroundUnlocked { get; set; } = false;

    /// <summary>自定义背景图片的路径</summary>
    public string CustomBackgroundImagePath { get; set; } = "";

    /// <summary>自定义背景图片的透明度</summary>
    public double CustomBackgroundOpacity { get; set; } = 0.2;

    /// <summary>隐藏模式下自定义的主题文字颜色 (为空时代表使用默认颜色)</summary>
    public string CustomThemeTextColor { get; set; } = "";

    /// <summary>自定义主色调 (为空时代表使用默认颜色)</summary>
    public string CustomThemeColor { get; set; } = "";

    /// <summary>主面板文本颜色 (为空时代表使用默认颜色)</summary>
    public string RightPanelTextColor { get; set; } = "";

    /// <summary>左侧按键字体横向间隔</summary>
    public double NavButtonLetterSpacing { get; set; } = 0.0;

    /// <summary>背景模糊程度</summary>
    public double BackgroundBlurRadius { get; set; } = 0.0;

    /// <summary>窗口透明效果: None / AcrylicBlur / Transparent</summary>
    public string WindowTransparencyMode { get; set; } = "None";

    /// <summary>应用主题: Dark / Light</summary>
    public string AppTheme { get; set; } = "Dark";

    /// <summary>窗口整体透明度 (0.1 ~ 1.0)</summary>
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>自定义字体名称（为空时使用默认字体）</summary>
    public string CustomFontFamily { get; set; } = "";

    /// <summary>自定义字体大小</summary>
    public double CustomFontSize { get; set; } = 14.0;

    /// <summary>以原版启动游戏（禁用 MelonLoader）</summary>
    public bool IsOriginalModeEnabled { get; set; } = false;

    /// <summary>启动游戏时隐藏控制台</summary>
    public bool HideConsoleOnLaunch { get; set; } = false;

    /// <summary>是否为第一次启动 (显示新手教程)</summary>
    public bool IsFirstLaunch { get; set; } = true;

    /// <summary>已屏蔽（不再显示）的公告 ID 列表</summary>
    public System.Collections.Generic.List<string> SuppressedAnnouncements { get; set; } = new();

    /// <summary>是否使用用户自定义的优选 IP</summary>
    public bool UseCustomIp { get; set; } = false;

    /// <summary>用户自定义的优选 IP 地址</summary>
    public string CustomIpAddress { get; set; } = "";

    /// <summary>用户已点击确认过的红点标记列表（格式: "版本号:标记名"，如 "1.2.6:Tutorial"）</summary>
    public System.Collections.Generic.List<string> DismissedBadges { get; set; } = new();

    /// <summary>整合包页是否使用列表模式</summary>
    public bool AlbumCollectionListMode { get; set; } = false;
}

public class NoticeInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; } = false;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("action_button_text")]
    public string ActionButtonText { get; set; } = "进入新版喵斯兔";

    [JsonPropertyName("cancel_button_text")]
    public string CancelButtonText { get; set; } = "不再显示该弹窗";
}

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ModLinks))]
[JsonSerializable(typeof(ModInfo[]))]
[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(NoticeInfo))]
[JsonSerializable(typeof(System.Collections.Generic.List<NoticeInfo>))]
[JsonSerializable(typeof(MirrorDomainsConfig))]
[JsonSerializable(typeof(MuseDashAccountInfo))]
[JsonSerializable(typeof(MdMoePlayerResponse))]
[JsonSerializable(typeof(Dictionary<string, MdMoeAlbum>))]
internal partial class AppJsonContext : JsonSerializerContext { }
