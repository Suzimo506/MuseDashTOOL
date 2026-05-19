using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Helpers;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Views;

namespace MdModManager.ViewModels;

public partial class ModManagerViewModel : ObservableObject
{
    private readonly IModCatalogService _catalogService;
    private readonly ILocalModService _localModService;
    private readonly IConfigService _configService;
    private readonly INotificationService _notificationService;
    private readonly ModStagingService _stagingService;
    private readonly INavigationService _navigationService;

    // 后台定时器：检测游戏进程，游戏关闭后自动将暂存文件移入 Mods 文件夹
    private Timer? _stagingWatcherTimer;

    [ObservableProperty]
    private ObservableCollection<LocalMod> _mods = new();

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0=Local, 1=Update, 2=Download

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _hasUpdates = false;

    [ObservableProperty]
    private bool _isUpdateTabEmpty = false;

    [ObservableProperty]
    private bool _isLocalTabEmpty = false;

    [ObservableProperty]
    private bool _isModsFolderMissing = false;

    [ObservableProperty]
    private int _modCount = 0;

    // 已被用户“不再提醒”的 Mod → 远端版本号（格式：name::version）
    // 当该 mod 又出现新版本时，对应的 key 就不同了，自然重新提醒
    private HashSet<string> _dismissedUpdateKeys = new(StringComparer.OrdinalIgnoreCase);

    // 是否显示粉色更新提示（有未被关闭的更新）
    public bool ShowUpdateBadge => _allLocalMods.Any(m =>
        m.HasUpdate && !_dismissedUpdateKeys.Contains(DismissKey(m)));

    // 只有在“更新”Tab下，并且该列表不为空，才显示“不再提醒”按钮
    public bool ShowDismissUpdateNoticeButton => IsUpdateTabSelected && !IsUpdateTabEmpty;

    // 生成“不再提醒”的 key：mod 名 + 远端版本
    private static string DismissKey(LocalMod m) =>
        $"{m.Name}::{m.RemoteInfo?.Version ?? ""}"; 

    private List<LocalMod> _allLocalMods = new();
    private List<LocalMod> _allRemoteMods = new();
    // 当前游戏版本（用于兼容性判断），空串表示未读取到
    private string _gameVersion = string.Empty;
    // 本地已安装的 FileName → LocalMod 映射（含 Name/Author/Version），用于精确匹配判断
    private Dictionary<string, LocalMod> _installedByFileName = new(StringComparer.OrdinalIgnoreCase);
    // 本地已安装的 FileName → Version 映射，用于下载列表版本比较
    private Dictionary<string, string> _installedVersions = new(StringComparer.OrdinalIgnoreCase);

    // 游戏运行时请求删除的 mod 文件路径集合，游戏关闭后执行实际删除
    private readonly HashSet<string> _pendingDeletePaths = new(StringComparer.OrdinalIgnoreCase);

    public ModManagerViewModel(
        IModCatalogService catalogService,
        ILocalModService localModService,
        IConfigService configService,
        INotificationService notificationService,
        ModStagingService stagingService,
        INavigationService navigationService)
    {
        _catalogService = catalogService;
        _localModService = localModService;
        _configService = configService;
        _notificationService = notificationService;
        _stagingService = stagingService;
        _navigationService = navigationService;
    }

    // ─── 游戏进程检测 ────────────────────────────────────────────────────────────
    private static bool IsGameRunning() =>
        Process.GetProcessesByName("MuseDash").Length > 0;

    // ─── 后台定时器：每 2 秒检测游戏是否结束，结束后自动迁移暂存文件 ───────────
    private void StartStagingWatcher()
    {
        _stagingWatcherTimer?.Dispose();
        _stagingWatcherTimer = new Timer(async _ => await OnStagingWatcherTickAsync(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private bool _wasGameRunning = false;

    private async Task OnStagingWatcherTickAsync()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return;

        bool running = IsGameRunning();

        // 游戏刚刚关闭，且暂存目录有文件 → 执行迁移
        if (_wasGameRunning && !running)
        {
            await FlushStagingToModsAsync(gamePath);
        }

        _wasGameRunning = running;
        _stagingService.Refresh(gamePath);
    }

    private async Task FlushStagingToModsAsync(string gamePath)
    {
        var stagingPath = _stagingService.GetStagingPath(gamePath);
        var modsPath = System.IO.Path.Combine(gamePath, "Mods");

        // 1. 迁移暂存文件
        int moved = 0;
        if (System.IO.Directory.Exists(stagingPath))
        {
            System.IO.Directory.CreateDirectory(modsPath);
            foreach (var file in System.IO.Directory.GetFiles(stagingPath, "*.dll"))
            {
                try
                {
                    var dest = System.IO.Path.Combine(modsPath, System.IO.Path.GetFileName(file));
                    await Task.Run(() => System.IO.File.Move(file, dest, overwrite: true));
                    moved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModStaging] 迁移文件 {file} 失败: {ex.Message}");
                }
            }
        }

        // 2. 执行待删除的 mod
        int deleted = 0;
        var toDelete = _pendingDeletePaths.ToList();
        foreach (var path in toDelete)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    await Task.Run(() => System.IO.File.Delete(path));
                    deleted++;
                }
                _pendingDeletePaths.Remove(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModStaging] 删除文件 {path} 失败: {ex.Message}");
            }
        }

        _stagingService.Refresh(gamePath);

        if (moved > 0 || deleted > 0)
        {
            var parts = new List<string>();
            if (moved > 0) parts.Add($"{moved} 个暂存 Mod 已安装");
            if (deleted > 0) parts.Add($"{deleted} 个 Mod 已删除");
            _notificationService.ShowSuccess(string.Join("，", parts) + "！");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                await InitializeAsync());
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await InitializeAsync(false, cancellationToken);
    
    public async Task InitializeAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        try
        {
            // 读取游戏版本（用于兼容性判断），失败时为空串（所有 mod 视为兼容）
            var gamePath = _configService.Config.GamePath;
            
            // 初始化 / 刷新暂存文件夹状态，并启动后台游戏检测定时器
            if (!string.IsNullOrEmpty(gamePath))
            {
                _stagingService.EnsureAndRefresh(gamePath);
                StartStagingWatcher();
            }

            // 检查 Mods 文件夹是否存在
            if (!string.IsNullOrEmpty(gamePath))
            {
                var modsPath = System.IO.Path.Combine(gamePath, "Mods");
                IsModsFolderMissing = !System.IO.Directory.Exists(modsPath);
            }
            else
            {
                IsModsFolderMissing = true;
            }

            _gameVersion = string.IsNullOrEmpty(gamePath)
                ? string.Empty
                : await _localModService.ReadGameVersionAsync(gamePath);

            Console.WriteLine($"[兼容性] 游戏路径: {gamePath}");
            Console.WriteLine($"[兼容性] 读取到的游戏版本: '{_gameVersion}'");

            var remoteMods = await _catalogService.GetModsAsync(forceRefresh, cancellationToken);

            // 把暂存文件夹路径传给 GetLocalMods，一次调用同时扫描 Mods/ 和 Mods_Staging/
            var stagingFolder = string.IsNullOrEmpty(gamePath)
                ? null
                : _stagingService.GetStagingPath(gamePath);
            _allLocalMods = _localModService.GetLocalMods(remoteMods, stagingFolder);

            // 标记本地列表的兼容性与检测配置文件
            var userDataPath = string.IsNullOrEmpty(gamePath) ? null : Path.Combine(gamePath, "UserData");
            foreach (var m in _allLocalMods)
            {
                var gv = m.RemoteInfo?.GameVersion;
                m.IsIncompatible = !string.IsNullOrEmpty(_gameVersion)
                    && !string.IsNullOrEmpty(gv)
                    && gv != "*"
                    && gv != _gameVersion;

                // 检测配置文件 (UserData/*.cfg 或 UserData/*.xml 或 UserData/[ModName]/)
                if (userDataPath != null && Directory.Exists(userDataPath))
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(m.FileName);
                    var cfgPath = Path.Combine(userDataPath, nameWithoutExt + ".cfg");
                    if (!File.Exists(cfgPath)) cfgPath = Path.Combine(userDataPath, nameWithoutExt + ".xml");

                    if (File.Exists(cfgPath))
                    {
                        m.ConfigFilePath = cfgPath;
                    }
                    else
                    {
                        // 尝试寻找同名文件夹
                        var folderPath = Path.Combine(userDataPath, nameWithoutExt);
                        if (Directory.Exists(folderPath))
                        {
                            m.ConfigFilePath = folderPath;
                        }
                    }
                }
            }

            // 将待删除的条目标记为 IsPendingDelete（用于 UI 红色显示）
            foreach (var m in _allLocalMods)
            {
                if (!string.IsNullOrEmpty(m.FilePath) &&
                    _pendingDeletePaths.Contains(m.FilePath))
                {
                    m.IsPendingDelete = true;
                }
            }


            // FileName → LocalMod 映射（仅实际安装到 Mods/ 的条目，排除暂存和待删除）
            _installedByFileName = _allLocalMods
                .Where(m => !string.IsNullOrEmpty(m.FilePath) && !m.IsStaged && !m.IsPendingDelete)
                .GroupBy(m => System.IO.Path.GetFileName(m.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // FileName -> Version 映射，同样排除暂存和待删除
            _installedVersions = _allLocalMods
                .Where(m => !string.IsNullOrEmpty(m.FilePath) && !m.IsStaged && !m.IsPendingDelete)
                .GroupBy(m => System.IO.Path.GetFileName(m.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);

            // 构建远端列表，并标记安装状态、版本和兼容性
            _allRemoteMods = remoteMods.Select(r =>
            {
                var localVersion = _installedVersions.TryGetValue(r.FileName, out var v) ? v : null;
                bool isInstalled = _installedByFileName.TryGetValue(r.FileName, out var localMod)
                    && IsModMatch(localMod, r);
                bool isIncompatible = !string.IsNullOrEmpty(_gameVersion)
                    && r.GameVersion != "*"
                    && r.GameVersion != _gameVersion;
                return new LocalMod
                {
                    Name = r.Name,
                    Version = r.Version,
                    Author = r.Author,
                    FilePath = "",
                    IsDisabled = false,
                    RemoteInfo = r,
                    IsLocallyInstalled = isInstalled,
                    LocalInstalledVersion = isInstalled ? localVersion : null,
                    IsIncompatible = isIncompatible
                };
            }).ToList();

            // 插入 .NET 6 环境项到首位 (自制谱必备)
            var dotnetInfo = new ModInfo
            {
                Name = ".NET 6 Runtime",
                Author = "Microsoft",
                Version = "SDK 6.0.428",
                Description = "自制谱游玩必备环境",
                HomePage = "https://dotnet.microsoft.com/zh-cn/download/dotnet/thank-you/sdk-6.0.428-windows-x64-installer",
                DownloadLink = "https://builds.dotnet.microsoft.com/dotnet/Sdk/6.0.428/dotnet-sdk-6.0.428-win-x64.exe",
                FileName = "dotnet6-runtime-placeholder",
                GameVersion = "*"
            };
            _allRemoteMods.Insert(0, new LocalMod
            {
                Name = dotnetInfo.Name,
                Version = dotnetInfo.Version,
                Author = dotnetInfo.Author,
                FilePath = "",
                IsDisabled = false,
                RemoteInfo = dotnetInfo,
                IsLocallyInstalled = false,
                IsIncompatible = false
            });

            var incompatibleRemote = _allRemoteMods.Where(m => m.IsIncompatible).ToList();
            Console.WriteLine($"[兼容性] 下载列表不兼容 mod 数量: {incompatibleRemote.Count}");
            foreach (var m in incompatibleRemote)
                Console.WriteLine($"[兼容性]   - {m.Name} (gameVersion={m.RemoteInfo?.GameVersion})");

            HasUpdates = _allLocalMods.Any(m => m.HasUpdate);
            OnPropertyChanged(nameof(ShowUpdateBadge));
            RefreshList();
        }
        catch (OperationCanceledException)
        {
            // Update was cancelled by switching tabs, ignore silently.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] InitializeAsync 异常: {ex}");
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshList();

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLocalTabSelected));
        OnPropertyChanged(nameof(IsUpdateTabSelected));
        OnPropertyChanged(nameof(IsDownloadTabSelected));
        RefreshList();
    }

    public bool IsLocalTabSelected => SelectedTabIndex == 0;
    public bool IsUpdateTabSelected => SelectedTabIndex == 1;
    public bool IsDownloadTabSelected => SelectedTabIndex == 2;

    /// <summary>当前列表无数据时为 true，用于 AXAML 控制列表区中央"空状态"面板的显示</summary>
    public bool IsListEmpty => ModCount == 0;

    /// <summary>根据远端 FileName 判断本地是否已安装（用于"再次下载"文本）</summary>
    public bool IsInstalled(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && _installedByFileName.ContainsKey(fileName);

    /// <summary>
    /// 判断本地 mod 和远端 mod 是否为同一个 mod。
    /// 文件名相同不代表是同一个 mod（可能是 fork 版本覆盖了同名文件）。
    /// 判断标准：名字规范化后一致（去掉空格和下划线，不区分大小写）。
    /// 注意：不用作者重叠来判断，因为 fork mod 往往与原版共享部分作者。
    /// </summary>
    private static bool IsModMatch(LocalMod local, ModInfo remote)
    {
        // 名字规范化：去掉空格和下划线，转小写，再比较
        static string NormName(string s) => s.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        return NormName(local.Name) == NormName(remote.Name);
    }

    private void RefreshList()
    {
        IEnumerable<LocalMod> sourceList;

        if (SelectedTabIndex == 0)
        {
            sourceList = _allLocalMods;
            IsUpdateTabEmpty = false;
            IsLocalTabEmpty = !sourceList.Any();
        }
        else if (SelectedTabIndex == 1)
        {
            sourceList = _allLocalMods.Where(m => m.HasUpdate);
            IsUpdateTabEmpty = !sourceList.Any();
        }
        else
        {
            sourceList = _allRemoteMods;
            IsUpdateTabEmpty = false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            sourceList = sourceList.Where(m =>
                (m.Name != null && m.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) ||
                (m.Author != null && m.Author.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            );
        }

        Mods = new ObservableCollection<LocalMod>(sourceList.ToList());
        ModCount = Mods.Count;
        // ModCount 改变时同步通知 IsListEmpty，驱动空状态面板的显示/隐藏
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(ShowDismissUpdateNoticeButton));
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand] private void SelectLocalTab() => SelectedTabIndex = 0;
    [RelayCommand] private void SelectUpdateTab() => SelectedTabIndex = 1;
    [RelayCommand] private void SelectDownloadTab() => SelectedTabIndex = 2;

    [RelayCommand]
    private async Task RefreshModListAsync()
    {
        await InitializeAsync(true);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            _notificationService.ShowFailure("打开失败", "游戏路径未设置");
            return;
        }

        var modsFolderPath = System.IO.Path.Combine(gamePath, "Mods");
        if (!System.IO.Directory.Exists(modsFolderPath))
        {
            _notificationService.ShowFailure("打开失败", "Mods 文件夹不存在");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = modsFolderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] OpenFolder 操作异常: {ex}");
            _notificationService.ShowFailure("打开失败", ex.Message);
        }
    }

    /// <summary>
    /// "不再提醒"按鈕命令：将当前更新列表中所有 mod 的当前远端版本加入已关闭集合。
    /// 如果这些 mod 未来又出了新版本，对应的 key 不同，会自动重新提醒。
    /// </summary>
    [RelayCommand]
    private void DismissUpdateNotice()
    {
        // 将当前列表中所有有更新的 mod+版本加入已关闭集合
        foreach (var mod in _allLocalMods.Where(m => m.HasUpdate))
        {
            _dismissedUpdateKeys.Add(DismissKey(mod));
        }
        RefreshList();
        // 通知 UI ShowUpdateBadge 已改变
        OnPropertyChanged(nameof(ShowUpdateBadge));
        OnPropertyChanged(nameof(ShowDismissUpdateNoticeButton));
    }

    [RelayCommand]
    private async Task ToggleModAsync(LocalMod mod)
    {
        if (SelectedTabIndex == 2 || string.IsNullOrEmpty(mod.FilePath)) return;

        try
        {
            if (mod.IsDisabled)
                await _localModService.EnableModAsync(mod);
            else
                await _localModService.DisableModAsync(mod);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] ToggleModAsync({mod.Name}) 操作异常: {ex}");
            _notificationService.ShowFailure("操作失败", ex.Message);
        }

        await InitializeAsync();
    }

    [RelayCommand]
    private async Task DeleteModAsync(LocalMod mod)
    {
        if (SelectedTabIndex == 2 || string.IsNullOrEmpty(mod.FilePath)) return;
        try
        {
            if (IsGameRunning() && !mod.IsStaged)
            {
                // 游戏运行中且非暂存 Mod → 标记为待删除，游戏关闭后自动删除的
                _pendingDeletePaths.Add(mod.FilePath);
                _stagingService.Refresh(_configService.Config.GamePath);
                _notificationService.ShowSuccess("将在游戏关闭后自动删除");
                await InitializeAsync();
            }
            else
            {
                // 游戏未运行，或是暂存 Mod（未被游戏加载锁定） → 直接删除
                await _localModService.DeleteModAsync(mod);
                _notificationService.ShowSuccess("删除成功");
                
                if (mod.IsStaged)
                {
                    _stagingService.Refresh(_configService.Config.GamePath);
                }
                
                await InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] DeleteModAsync({mod.Name}) 操作异常: {ex}");
            _notificationService.ShowFailure("删除失败", ex.Message);
        }
    }

    [RelayCommand]
    public async Task DownloadModAsync(LocalMod mod)
    {
        if (mod.RemoteInfo == null) return;
        await PerformDownloadAsync(mod.RemoteInfo);
    }

    [RelayCommand]
    private async Task UpdateModAsync(LocalMod mod)
    {
        // .NET 6 特殊处理
        if (mod.RemoteInfo?.FileName == "dotnet6-runtime-placeholder")
        {
            try { Process.Start(new ProcessStartInfo(mod.RemoteInfo.HomePage) { UseShellExecute = true }); }
            catch (Exception ex) { Console.WriteLine($"[ModManagerViewModel] UpdateModAsync (.NET 6) 跳转异常: {ex}"); }
            return;
        }

        if (mod.RemoteInfo == null) return;
        await PerformDownloadAsync(mod.RemoteInfo, isUpdate: true, localMod: mod);
    }

    /// <summary>打开 Mod 详情页 — .NET 6 特殊处理，其余跳转 Euterpe</summary>
    [RelayCommand]
    private void OpenHomePage(LocalMod mod)
    {
        // .NET 6 特殊处理
        if (mod.RemoteInfo?.FileName == "dotnet6-runtime-placeholder")
        {
            try { Process.Start(new ProcessStartInfo(mod.RemoteInfo.HomePage) { UseShellExecute = true }); }
            catch (Exception ex) { Console.WriteLine($"[ModManagerViewModel] OpenHomePage (.NET 6) 操作异常: {ex}"); }
            return;
        }

        try { Process.Start(new ProcessStartInfo("https://github.com") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] OpenHomePage({mod.Name}) 操作异常: {ex}");
            _notificationService.ShowFailure("", ex.Message);
        }
    }

    /// <summary>
    /// 导入本地 Mod 文件 (.dll) 到游戏的 Mods 目录（游戏运行时暂存）
    /// </summary>
    /// <param name="sourcePath">源文件路径</param>
    public async Task ImportModFileAsync(string sourcePath)
    {
        try
        {
            var gamePath = _configService.Config.GamePath;
            if (string.IsNullOrEmpty(gamePath))
            {
                _notificationService.ShowFailure("导入失败", "游戏路径未设置");
                return;
            }

            var fileName = System.IO.Path.GetFileName(sourcePath);

            if (IsGameRunning())
            {
                // 游戏运行中 → 放入暂存文件夹
                var stagingPath = _stagingService.GetStagingPath(gamePath);
                System.IO.Directory.CreateDirectory(stagingPath);
                var targetPath = System.IO.Path.Combine(stagingPath, fileName);
                await Task.Run(() => System.IO.File.Copy(sourcePath, targetPath, true));
                _stagingService.Refresh(gamePath);
                _notificationService.ShowSuccess("已暂存，将在游戏关闭后自动安装");
            }
            else
            {
                // 游戏未运行 → 直接复制到 Mods 文件夹
                var modsFolderPath = System.IO.Path.Combine(gamePath, "Mods");
                System.IO.Directory.CreateDirectory(modsFolderPath);
                var targetPath = System.IO.Path.Combine(modsFolderPath, fileName);
                await Task.Run(() => System.IO.File.Copy(sourcePath, targetPath, true));
                _notificationService.ShowSuccess("导入成功");
            }
            
            // 重新初始化以刷新列表
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] ImportModFileAsync({sourcePath}) 操作异常: {ex}");
            _notificationService.ShowFailure("导入失败", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenModConfig(LocalMod mod)
    {
        if (string.IsNullOrEmpty(mod.ConfigFilePath) || (!File.Exists(mod.ConfigFilePath) && !Directory.Exists(mod.ConfigFilePath)))
        {
            _notificationService.ShowFailure("跳转失败", "找不到配置文件或文件夹");
            return;
        }

        _navigationService.RequestNavigateToConfig(mod.ConfigFilePath);
    }

    private bool IsDotNet6Installed()
    {
        return DotNetRuntimeHelper.IsDotNet6Installed();
    }

    private async Task PerformDownloadAsync(ModInfo remoteInfo, bool isUpdate = false, LocalMod? localMod = null)
    {
        var fileName = !string.IsNullOrEmpty(remoteInfo.FileName)
            ? remoteInfo.FileName
            : remoteInfo.DownloadLink.Replace("Mods/", "");

        if (string.IsNullOrEmpty(fileName)) return;

        // 下载任何 mod 前检测 .NET 6 环境 (.NET 6 运行时本身的下载页除外)
        if (fileName != "dotnet6-runtime-placeholder" && !IsDotNet6Installed())
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow as MainWindow : null;
            if (mainWindow != null)
            {
                await mainWindow.ShowMessageBoxAsync("请先安装.net6环境！");
                return;
            }
        }

        // 特殊处理 .NET 6 运行时下载：使用直链触发浏览器下载
        if (fileName == "dotnet6-runtime-placeholder")
        {
            try
            {
                var url = !string.IsNullOrEmpty(remoteInfo.DownloadLink) ? remoteInfo.DownloadLink : remoteInfo.HomePage;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                _notificationService.ShowSuccess("正在开始下载 .NET 6 运行时");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModManagerViewModel] PerformDownloadAsync(.NET 6) 操作异常: {ex}");
                _notificationService.ShowFailure(".NET 6", ex.Message);
            }
            return;
        }

        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
        {
            _notificationService.ShowFailure(remoteInfo.Name, "游戏路径未设置");
            return;
        }

        string downloadUrl;
        if (remoteInfo.Source == "Euterpe")
        {
            var domain = MdModManager.Helpers.MirrorDomainRegistry.DownloadDomain;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = "download.suzimo.site";
            }
            var protocol = domain.StartsWith("http") ? "" : "https://";
            downloadUrl = $"{protocol}{domain}/Mods/{fileName}";
        }
        else
        {
            // 默认 Gitee 源
            downloadUrl = $"https://gitee.com/lxymahatma/ModLinks/raw/dev/Mods/{fileName}";
        }

        // 游戏运行时 → 下载后放入暂存文件夹
        bool gameRunning = IsGameRunning();
        var targetPath = gameRunning
            ? System.IO.Path.Combine(_stagingService.GetStagingPath(gamePath), fileName)
            : System.IO.Path.Combine(gamePath, "Mods", fileName);

        try
        {
            using var client = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30));
            var bytes = await client.GetByteArrayAsync(downloadUrl);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);

            // 如果是更新，尝试删除旧文件（游戏未运行时才能直接删除）
            if (isUpdate && localMod != null && !string.IsNullOrEmpty(localMod.FilePath) && !gameRunning)
            {
                if (System.IO.File.Exists(localMod.FilePath))
                {
                    try { System.IO.File.Delete(localMod.FilePath); }
                    catch (Exception deleteEx)
                    {
                        Console.WriteLine($"[ModManagerViewModel] 无法删除旧版本 Mod '{localMod.FilePath}': {deleteEx}");
                    }
                }
            }

            await System.IO.File.WriteAllBytesAsync(targetPath, bytes);

            if (gameRunning)
            {
                _stagingService.Refresh(gamePath);
                _notificationService.ShowSuccess("已暂存，将在游戏关闭后自动安装");
            }
            else
            {
                _notificationService.ShowSuccess(isUpdate ? "更新成功" : "下载成功");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModManagerViewModel] PerformDownloadAsync({remoteInfo.Name}) 操作异常: {ex}");
            _notificationService.ShowFailure(remoteInfo.Name, ex.Message);
        }

        await InitializeAsync();
    }


}
