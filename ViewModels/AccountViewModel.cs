using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Services;
using MdModManager.Models;
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

    // 存储全部数据，用于分批加载，避免一次性创建过多 UI 元素
    private readonly List<PlayerSongRecord> _allRecentPlays = new();

    public ObservableCollection<PlayerSongRecord> RecentPlays { get; } = new();

    public void LoadMore()
    {
        int currentCount = RecentPlays.Count;
        int maxCount = _allRecentPlays.Count;
        if (currentCount >= maxCount) return;

        int nextCount = System.Math.Min(currentCount + 15, maxCount);
        for (int i = currentCount; i < nextCount; i++)
        {
            RecentPlays.Add(_allRecentPlays[i]);
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

        RecentPlays.Clear();
        LoadMore(); // 初始加载前 15 条
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
