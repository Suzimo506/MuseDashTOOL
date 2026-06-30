using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Services;
using MdModManager.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace MdModManager.ViewModels;

public partial class AccountViewModel : ObservableObject
{
    [ObservableProperty]
    private string _nickname = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Not Logged In" : "未登录";

    [ObservableProperty]
    private string _uid = "-";

    [ObservableProperty]
    private string _relativeLevel = "『0.000』";

    [ObservableProperty]
    private int _recordsCount = 0;

    [ObservableProperty]
    private int _perfectsCount = 0;

    [ObservableProperty]
    private string _averageAccuracy = "0.00 %";

    [ObservableProperty]
    private bool _isLoggedIn = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualUid))]
    [NotifyCanExecuteChangedFor(nameof(SaveManualUidCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusMessage = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Reading Account Info..." : "正在读取账号信息...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveManualUid))]
    [NotifyCanExecuteChangedFor(nameof(SaveManualUidCommand))]
    private string _manualUidText = MuseDashAccountService.GetManualMuseDashUid();

    [ObservableProperty]
    private bool _isManualUidInputVisible;

    [ObservableProperty]
    private bool _isUsingManualUid;

    public bool CanSaveManualUid => !IsLoading && MuseDashAccountService.IsValidManualUid(ManualUidText);

    // 排序方式: 0=默认, 1=排名最高, 2=准确率最高, 3=最难
    [ObservableProperty]
    private int _sortMode = 0;

    [ObservableProperty]
    private string _sortLabel = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Default Sort" : "默认排序";

    // 搜索相关
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _searchStatus = "";

    [ObservableProperty]
    private bool _hasSearchResults = false;

    // 搜索结果匹配索引列表
    private List<int> _searchMatchIndices = new();
    private int _currentSearchIndex = -1;

    // 当搜索有结果时，触发滚动到指定项的事件
    public event Action<int>? ScrollToItemRequested;

    partial void OnSearchTextChanged(string value)
    {
        // 内容为空时自动取消搜索模式并清除粉色标粉
        if (string.IsNullOrWhiteSpace(value))
        {
            ClearSearchState();
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        SaveManualUidCommand.NotifyCanExecuteChanged();
    }

    // 存储全部数据，用于分批加载，避免一次性创建过多 UI 元素
    private readonly List<PlayerSongRecord> _allRecentPlays = new();

    // 排序后的视图，当前展示的数据源
    private List<PlayerSongRecord> _sortedPlays = new();

    public ObservableCollection<PlayerSongRecord> RecentPlays { get; } = new();

    public void LoadMore()
    {
        int currentCount = RecentPlays.Count;
        int maxCount = _sortedPlays.Count;
        if (currentCount >= maxCount) return;

        int nextCount = Math.Min(currentCount + 15, maxCount);
        for (int i = currentCount; i < nextCount; i++)
        {
            RecentPlays.Add(_sortedPlays[i]);
        }
    }

    // 确保所有数据都已加载到 UI 集合中
    private void EnsureAllLoaded()
    {
        while (RecentPlays.Count < _sortedPlays.Count)
        {
            RecentPlays.Add(_sortedPlays[RecentPlays.Count]);
        }
    }

    public void Cleanup()
    {
        // 离开界面时释放前 15 条之后的记录
        while (RecentPlays.Count > 15)
        {
            RecentPlays.RemoveAt(RecentPlays.Count - 1);
        }
    }

    [RelayCommand]
    private void CycleSortMode()
    {
        SortMode = (SortMode + 1) % 4;
        ApplySortAndReload();
    }

    // 应用排序并刷新列表
    private void ApplySortAndReload()
    {
        switch (SortMode)
        {
            case 1:
                SortLabel = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Rank Sort" : "排名排序";
                _sortedPlays = _allRecentPlays.OrderBy(r => r.RawRank).ToList();
                break;
            case 2:
                SortLabel = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Accuracy Sort" : "准确率排序";
                _sortedPlays = _allRecentPlays.OrderByDescending(r => r.RawAccuracy).ToList();
                break;
            case 3:
                SortLabel = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Difficulty Sort" : "难度排序";
                _sortedPlays = _allRecentPlays.OrderByDescending(r => r.RawDifficulty).ToList();
                break;
            default:
                SortLabel = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "Default Sort" : "默认排序";
                _sortedPlays = _allRecentPlays.ToList();
                break;
        }

        // 重新编号
        for (int i = 0; i < _sortedPlays.Count; i++)
            _sortedPlays[i].DisplayIndex = i + 1;

        RecentPlays.Clear();
        LoadMore();

        // 排序后清除搜索状态
        ClearSearchState();
    }

    [RelayCommand]
    private void SearchConfirm()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ClearSearchState();
            return;
        }

        // 确保全部数据已加载到 UI，方便跳转
        EnsureAllLoaded();

        var keyword = SearchText.Trim();

        // 先清除上次高亮
        foreach (var idx in _searchMatchIndices)
            if (idx < _sortedPlays.Count) _sortedPlays[idx].IsSearchMatch = false;
        _searchMatchIndices.Clear();

        for (int i = 0; i < _sortedPlays.Count; i++)
        {
            var r = _sortedPlays[i];
            if (r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _searchMatchIndices.Add(i);
                r.IsSearchMatch = true;
            }
        }

        if (_searchMatchIndices.Count == 0)
        {
            SearchStatus = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "No Results" : "无结果";
            HasSearchResults = false;
            _currentSearchIndex = -1;
            return;
        }

        HasSearchResults = true;
        _currentSearchIndex = 0;
        JumpToCurrentMatch();
    }

    [RelayCommand]
    private void SearchPrev()
    {
        if (_searchMatchIndices.Count == 0) return;
        _currentSearchIndex--;
        if (_currentSearchIndex < 0) _currentSearchIndex = _searchMatchIndices.Count - 1;
        JumpToCurrentMatch();
    }

    [RelayCommand]
    private void SearchNext()
    {
        if (_searchMatchIndices.Count == 0) return;
        _currentSearchIndex++;
        if (_currentSearchIndex >= _searchMatchIndices.Count) _currentSearchIndex = 0;
        JumpToCurrentMatch();
    }

    private void JumpToCurrentMatch()
    {
        if (_currentSearchIndex < 0 || _currentSearchIndex >= _searchMatchIndices.Count) return;

        int itemIndex = _searchMatchIndices[_currentSearchIndex];
        SearchStatus = $"{_currentSearchIndex + 1}/{_searchMatchIndices.Count}";
        ScrollToItemRequested?.Invoke(itemIndex);
    }

    private void ClearSearchState()
    {
        // 清除所有高亮标记
        foreach (var idx in _searchMatchIndices)
            if (idx < _sortedPlays.Count) _sortedPlays[idx].IsSearchMatch = false;
        _searchMatchIndices.Clear();
        _currentSearchIndex = -1;
        SearchStatus = "";
        HasSearchResults = false;
    }

    public async Task InitializeAsync()
    {
        RecentPlays.Clear();

        // ── Fast path: prefetch already finished ─────────────────────────────
        if (MuseDashAccountService.CachedProfile != null &&
            MuseDashAccountService.CachedAccountInfo != null)
        {
            IsLoading = false;
            ApplyProfile(MuseDashAccountService.CachedAccountInfo,
                         MuseDashAccountService.CachedProfile);
            return;
        }

        bool isEn = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US";

        // ── Prefetch in progress: show subtle spinner, await completion ───────
        IsLoading = true;
        StatusMessage = isEn ? "Syncing data..." : "正在同步数据...";

        await Task.Yield();

        // Wait for the background prefetch (nearly done by the time user clicks)
        await MuseDashAccountService.WaitForPrefetchAsync();

        if (MuseDashAccountService.CachedProfile != null &&
            MuseDashAccountService.CachedAccountInfo != null)
        {
            IsLoading = false;
            ApplyProfile(MuseDashAccountService.CachedAccountInfo,
                         MuseDashAccountService.CachedProfile);
            return;
        }

        // ── Fallback: prefetch failed entirely, try a fresh fetch ─────────────
        StatusMessage = isEn ? "Fetching data from musedash.moe..." : "正在从 musedash.moe 获取数据...";

        var info = await Task.Run(() => MuseDashAccountService.ReadAccountInfo());
        if (info == null)
        {
            IsLoggedIn = false;
            Nickname = isEn ? "Not Logged In" : "未登录";
            ManualUidText = MuseDashAccountService.GetManualMuseDashUid();
            IsManualUidInputVisible = true;
            IsUsingManualUid = false;
            StatusMessage = isEn
                ? "Login info not found. Enter your Muse Dash UID manually to use account features."
                : "未找到登录信息，可手动输入 Muse Dash UID 使用账号功能。";
            IsLoading = false;
            return;
        }

        IsManualUidInputVisible = info.IsManual;
        IsUsingManualUid = info.IsManual;
        if (info.IsManual)
            ManualUidText = info.Uid ?? "";

        Uid = info.Uid ?? "-";
        Nickname = isEn ? "Loading..." : "正在加载...";

        var profile = await MuseDashAccountService.FetchPlayerProfileAsync(info.Uid ?? "");
        IsLoading = false;

        if (profile != null)
        {
            ApplyProfile(info, profile);
        }
        else
        {
            var rawNick = info.Nickname ?? info.Username ?? info.Uid ?? (isEn ? "Player" : "玩家");
            Nickname = IsLikelyUid(rawNick) ? (isEn ? "(Nickname not set)" : "（未设置昵称）") : rawNick;
            var reason = MuseDashAccountService.LastError ?? (isEn ? "Network unreachable" : "网络不可达");
            StatusMessage = isEn ? $"Connection failed: {reason}" : $"连接失败：{reason}";
            IsManualUidInputVisible = info.IsManual;
            IsUsingManualUid = info.IsManual;
        }
    }

    private void ApplyProfile(MuseDashAccountInfo info, PlayerProfileData profile)
    {
        bool isEn = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US";
        IsLoggedIn = true;
        IsManualUidInputVisible = info.IsManual;
        IsUsingManualUid = info.IsManual;
        if (info.IsManual)
            ManualUidText = info.Uid ?? "";
        Uid = info.Uid ?? "-";
        Nickname = string.IsNullOrWhiteSpace(profile.Nickname)
            ? (info.Nickname ?? (isEn ? "Player" : "玩家"))
            : profile.Nickname;
        RelativeLevel = $"『{profile.RelativeLevel:0.000}』";
        RecordsCount = profile.RecordsCount;
        PerfectsCount = profile.PerfectsCount;
        AverageAccuracy = $"{profile.AverageAccuracy:0.00} %";
        
        StatusMessage = info.IsManual
            ? (isEn ? "Data synced with manual UID" : "已使用手动 UID 同步数据")
            : (isEn ? "Data Synced" : "数据已同步");

        _allRecentPlays.Clear();
        _allRecentPlays.AddRange(profile.RecentPlays);

        // 重置排序为默认
        SortMode = 0;
        SortLabel = isEn ? "Default Sort" : "默认排序";
        _sortedPlays = _allRecentPlays.ToList();

        RecentPlays.Clear();
        LoadMore(); // 初始加载前 15 条

        ClearSearchState();
        SearchText = "";
    }

    [RelayCommand]
    private async Task Refresh()
    {
        // Invalidate cache on manual refresh so fresh data is fetched
        MuseDashAccountService.InvalidateCache();
        MuseDashAccountService.StartPrefetch();
        await InitializeAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSaveManualUid))]
    private async Task SaveManualUidAsync()
    {
        bool isEn = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US";
        var uid = ManualUidText.Trim();
        if (!MuseDashAccountService.IsValidManualUid(uid))
        {
            StatusMessage = isEn ? "Please enter a valid Muse Dash UID." : "请输入有效的 Muse Dash UID。";
            return;
        }

        IsLoading = true;
        StatusMessage = isEn ? "Saving manual UID..." : "正在保存手动 UID...";

        try
        {
            await MuseDashAccountService.SaveManualMuseDashUidAsync(uid);
            MuseDashAccountService.StartPrefetch();

            var info = MuseDashAccountService.ReadManualAccountInfo();
            if (info == null)
            {
                StatusMessage = isEn ? "Manual UID was not saved." : "手动 UID 未保存成功。";
                return;
            }

            var profile = await MuseDashAccountService.FetchPlayerProfileAsync(info.Uid ?? "");
            if (profile != null)
            {
                ApplyProfile(info, profile);
                StatusMessage = isEn ? "Manual UID saved and data synced." : "手动 UID 已保存并同步数据。";
            }
            else
            {
                IsLoggedIn = false;
                IsManualUidInputVisible = true;
                IsUsingManualUid = true;
                Uid = info.Uid ?? "-";
                Nickname = isEn ? "(Manual UID)" : "（手动 UID）";
                var reason = MuseDashAccountService.LastError ?? (isEn ? "Profile not found" : "未找到玩家资料");
                StatusMessage = isEn ? $"Manual UID saved, but profile fetch failed: {reason}" : $"手动 UID 已保存，但拉取资料失败：{reason}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = isEn ? $"Failed to save manual UID: {ex.Message}" : $"保存手动 UID 失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static bool IsLikelyUid(string s)
    {
        if (s.Length < 16) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }
}
