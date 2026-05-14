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
    private string _nickname = "未登录";

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
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusMessage = "正在读取账号信息...";

    // 排序方式: 0=默认, 1=排名最高, 2=准确率最高, 3=最难
    [ObservableProperty]
    private int _sortMode = 0;

    [ObservableProperty]
    private string _sortLabel = "默认排序";

    // 搜索相关
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _searchStatus = "";

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
                SortLabel = "排名排序";
                _sortedPlays = _allRecentPlays.OrderBy(r => r.RawRank).ToList();
                break;
            case 2:
                SortLabel = "准确率排序";
                _sortedPlays = _allRecentPlays.OrderByDescending(r => r.RawAccuracy).ToList();
                break;
            case 3:
                SortLabel = "难度排序";
                _sortedPlays = _allRecentPlays.OrderByDescending(r => r.RawDifficulty).ToList();
                break;
            default:
                SortLabel = "默认排序";
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
            SearchStatus = "无结果";
            _currentSearchIndex = -1;
            return;
        }

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

        // ── Prefetch in progress: show subtle spinner, await completion ───────
        IsLoading = true;
        StatusMessage = "正在同步数据...";

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
        StatusMessage = "正在从 musedash.moe 获取数据...";

        var info = await Task.Run(() => MuseDashAccountService.ReadAccountInfo());
        if (info == null)
        {
            IsLoggedIn = false;
            Nickname = "未登录";
            StatusMessage = "未找到登录信息，请先打开喵斯快跑并登录。";
            IsLoading = false;
            return;
        }

        Uid = info.Uid ?? "-";
        Nickname = "正在加载...";

        var profile = await MuseDashAccountService.FetchPlayerProfileAsync(info.Uid ?? "");
        IsLoading = false;

        if (profile != null)
        {
            ApplyProfile(info, profile);
        }
        else
        {
            var rawNick = info.Nickname ?? info.Username ?? info.Uid ?? "玩家";
            Nickname = IsLikelyUid(rawNick) ? "（未设置昵称）" : rawNick;
            var reason = MuseDashAccountService.LastError ?? "网络不可达";
            StatusMessage = $"连接失败：{reason}";
        }
    }

    private void ApplyProfile(MuseDashAccountInfo info, PlayerProfileData profile)
    {
        IsLoggedIn = true;
        Uid = info.Uid ?? "-";
        Nickname = string.IsNullOrWhiteSpace(profile.Nickname)
            ? (info.Nickname ?? "玩家")
            : profile.Nickname;
        RelativeLevel = $"『{profile.RelativeLevel:0.000}』";
        RecordsCount = profile.RecordsCount;
        PerfectsCount = profile.PerfectsCount;
        AverageAccuracy = $"{profile.AverageAccuracy:0.00} %";
        StatusMessage = "数据已同步";

        _allRecentPlays.Clear();
        _allRecentPlays.AddRange(profile.RecentPlays);

        // 重置排序为默认
        SortMode = 0;
        SortLabel = "默认排序";
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

    private static bool IsLikelyUid(string s)
    {
        if (s.Length < 16) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }
}
