using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;

namespace MdModManager.Models;

/// <summary>本地 Mods 文件夹中的单个 Mod 状态</summary>
public partial class LocalMod : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _version = "";

    [ObservableProperty]
    private string _author = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private bool _isDisabled;

    /// <summary>游戏运行中导入、暂存在 Mods_Staging 文件夹的 Mod（重启游戏后生效）</summary>
    [ObservableProperty]
    private bool _isStaged;

    /// <summary>游戏运行中请求删除、将在游戏关闭后自动删除（UI 显示红色加粗名字）</summary>
    [ObservableProperty]
    private bool _isPendingDelete;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>展开时显示的 Mod 描述文本。优先用 RemoteInfo.Description，若为空显示"暂无信息"。</summary>
    [ObservableProperty]
    private string _description = "";

    /// <summary>是否正在加载 GitHub 描述</summary>
    [ObservableProperty]
    private bool _isLoadingDescription;

    // 是否正在下载
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>原始英文描述缓存</summary>
    private string _originalDescription = "";
    
    /// <summary>已翻译的中文描述缓存</summary>
    private string _translatedDescription = "";

    /// <summary>展开/折叠命令</summary>
    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded)
        {
            _ = UpdateDescriptionAsync();
        }
    }

    private async System.Threading.Tasks.Task UpdateDescriptionAsync()
    {
        if (IsLoadingDescription) return;

        bool autoTranslate = false;
        try 
        {
            var configService = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<Services.IConfigService>();
            if (configService != null) autoTranslate = configService.Config.AutoTranslateDescriptions;
        } 
        catch { }

        // 1. 如果尚未获取原版描述，则去获取
        if (string.IsNullOrEmpty(_originalDescription))
        {
            IsLoadingDescription = true;
            Description = "正在加载...";

            string fetchedDesc = "";
            var desc = RemoteInfo?.Description;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                fetchedDesc = desc;
            }
            else if (!string.IsNullOrWhiteSpace(RemoteInfo?.HomePage))
            {
                fetchedDesc = await FetchGitHubDescriptionAsync(RemoteInfo.HomePage);
            }
            else
            {
                fetchedDesc = "暂无信息";
            }
            
            _originalDescription = fetchedDesc;
            IsLoadingDescription = false;
        }

        // 2. 判断是否需要翻译，且不需要对“暂无信息”进行翻译
        if (autoTranslate && _originalDescription != "暂无信息")
        {
            if (string.IsNullOrEmpty(_translatedDescription))
            {
                IsLoadingDescription = true;
                Description = "正在翻译...";
                _translatedDescription = await Helpers.BingTranslateHelper.TranslateToChineseAsync(_originalDescription);
                IsLoadingDescription = false;
            }
            Description = string.IsNullOrEmpty(_translatedDescription) ? _originalDescription : _translatedDescription;
        }
        else
        {
            Description = _originalDescription;
        }
    }

    private async System.Threading.Tasks.Task<string> FetchGitHubDescriptionAsync(string repo)
    {
        string repoPath;
        if (repo.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(repo);
            repoPath = uri.AbsolutePath.Trim('/');
        }
        else
        {
            repoPath = repo.Trim('/');
        }

        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MdModManager/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var apiUrl = $"https://api.github.com/repos/{repoPath}";
            var json = await client.GetStringAsync(apiUrl);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var githubDesc = doc.RootElement.TryGetProperty("description", out var prop)
                ? prop.GetString()
                : null;

            return string.IsNullOrWhiteSpace(githubDesc) ? "暂无信息" : githubDesc;
        }
        catch
        {
            return "暂无信息";
        }
    }

    /// <summary>供下载列表"再次下载"按钮文本使用，由 ViewModel 在构建时赋值</summary>
    public bool IsLocallyInstalled { get; set; }

    /// <summary>下载列表中未安装状态（= !IsLocallyInstalled），用于 XAML 条件显示</summary>
    public bool IsNotLocallyInstalled => !IsLocallyInstalled;

    /// <summary>
    /// 仅在"下载"列表（FilePath 为空）且本地未安装且不兼容时显示"未安装"徽章。
    /// 本地/更新列表的条目 FilePath 非空，不应显示"未安装"文字。
    /// </summary>
    public bool ShowNotInstalledBadge => string.IsNullOrEmpty(FilePath) && !IsLocallyInstalled && !IsIncompatible && FileName != "dotnet6-runtime-placeholder";

    /// <summary>该 mod 与当前游戏版本不兼容（GameVersion 非"*"且不匹配游戏版本），由 ViewModel 赋值</summary>
    [ObservableProperty]
    private bool _isIncompatible;

    /// <summary>下载列表中对应的本地已安装版本（用于版本比较状态显示）</summary>
    public string? LocalInstalledVersion { get; set; }

    /// <summary>对应的 ModLinks 远端信息（null = 本地独有，不在 ModLinks 中）</summary>
    public ModInfo? RemoteInfo { get; set; }

    /// <summary>
    /// 只有当远端版本严格大于本地版本时才判定为有更新。
    /// 使用 System.Version 进行 SemVer 语义比较，忽略版本号前缀 'v'。
    /// </summary>
    public bool HasUpdate
    {
        get
        {
            if (RemoteInfo is null || string.IsNullOrEmpty(Version)) return false;

            var localStr = Version.TrimStart('v', 'V');
            var remoteStr = RemoteInfo.Version.TrimStart('v', 'V');

            if (System.Version.TryParse(localStr, out var localVer) &&
                System.Version.TryParse(remoteStr, out var remoteVer))
            {
                return remoteVer > localVer; // 远端严格更新才算有更新
            }

            // fallback: 字符串相同则无更新，不同才提示
            return !string.Equals(localStr, remoteStr, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>远端是否有更高版本可用（用于下载列表显示"有更新可用"）</summary>
    public bool DownloadListHasUpdate
    {
        get
        {
            if (!IsLocallyInstalled || RemoteInfo is null) return false;
            if (string.IsNullOrEmpty(LocalInstalledVersion)) return false;

            var localStr = LocalInstalledVersion.TrimStart('v', 'V');
            var remoteStr = RemoteInfo.Version.TrimStart('v', 'V');

            if (System.Version.TryParse(localStr, out var localVer) &&
                System.Version.TryParse(remoteStr, out var remoteVer))
            {
                return remoteVer > localVer;
            }

            return !string.Equals(localStr, remoteStr, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>本地已安装且版本相同或更高（用于下载列表显示"最新版本"）</summary>
    public bool DownloadListIsUpToDate => IsLocallyInstalled && !DownloadListHasUpdate;

    public string FileName => !string.IsNullOrEmpty(FilePath)
        ? Path.GetFileName(FilePath)
        : RemoteInfo?.FileName ?? "";

    /// <summary>可以执行更新操作 = 有更新 AND 本地文件存在（排除下载Tab中的远端条目）</summary>
    public bool CanUpdate => HasUpdate && !string.IsNullOrEmpty(FilePath);

    /// <summary>远端版本号文本（用于状态栏显示"有更新可用: x.x.x"）</summary>
    public string RemoteVersionText => RemoteInfo?.Version ?? "";

    /// <summary>本地列表中"有更新可用"的状态显示文本</summary>
    public string UpdateAvailableText => $"有更新可用：{RemoteVersionText}";

    /// <summary>Mod 对应的配置文件路径 (UserData 目录下)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigFile))]
    private string? _configFilePath;

    /// <summary>是否存在配置文件</summary>
    public bool HasConfigFile => !string.IsNullOrEmpty(ConfigFilePath);
}
