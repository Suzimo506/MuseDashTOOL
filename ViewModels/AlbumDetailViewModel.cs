using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.Views;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using MdModManager.Helpers;

namespace MdModManager.ViewModels;

public partial class AlbumDetailViewModel : ObservableObject, IDisposable
{
    private string _chartCountText = string.Empty;
    private static readonly SemaphoreSlim _coverSemaphore = new(7);

    [ObservableProperty]
    private string _statusMessage = string.Empty;
    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private readonly IAlbumCollectionService _collectionService;
    private readonly IConfigService _configService;
    private readonly IDownloadManagerService _downloadManagerService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private DesignerCategory? _category;

    [ObservableProperty]
    private ObservableCollection<MdmcChart> _charts = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    public AlbumDetailViewModel(
        ChartDownloadViewModel chartDownloadViewModel,
        IAlbumCollectionService collectionService,
        IConfigService configService,
        IDownloadManagerService downloadManagerService,
        INotificationService notificationService)
    {
        _chartDownloadViewModel = chartDownloadViewModel;
        _collectionService = collectionService;
        _configService = configService;
        _downloadManagerService = downloadManagerService;
        _notificationService = notificationService;

        _chartDownloadViewModel.PropertyChanged += OnChartDownloadViewModelPropertyChanged;
    }

    private void OnChartDownloadViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartDownloadViewModel.PreviewStatusText))
        {
            UpdateStatusMessage();
        }
    }

    public void Dispose()
    {
        _chartDownloadViewModel.PropertyChanged -= OnChartDownloadViewModelPropertyChanged;
    }

    private void UpdateStatusMessage()
    {
        if (!string.IsNullOrEmpty(_chartDownloadViewModel.PreviewStatusText))
        {
            StatusMessage = _chartDownloadViewModel.PreviewStatusText;
        }
        else
        {
            StatusMessage = _chartCountText;
        }
    }

    public async Task InitializeAsync(DesignerCategory category, string searchText = "")
    {
        Log($"Initializing album detail for '{category.Name}' with search query '{searchText}'.");
        Category = category;
        SearchText = searchText;
        Charts.Clear();
        IsLoading = true;
        IsEmpty = false;

        var charts = await _collectionService.GetChartsAsync(category.Name);
        Category.Charts = charts;
        Log($"Category '{category.Name}' returned {charts.Count} chart records.");

        // 之前在这里过滤了谱面，导致只显示一个谱面。
        // 根据要求，现在即使有搜索词也显示全部谱面（"不要只显示这一个谱面，而是打开这个谱面所在的文件夹"）。
        // 匹配到的谱面依然会由前端通过 SearchText 属性进行粉色高亮。
        // var filteredCharts = charts;
        // if (!string.IsNullOrWhiteSpace(searchText))
        // {
        //     var normalizedQuery = searchText.Trim().ToLowerInvariant();
        //     filteredCharts = charts.Where(c => 
        //         c.Title?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
        //         c.Author?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true ||
        //         c.Artist?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true
        //     ).ToList();
        // }

        foreach (var c in charts) // Iterate over all charts, not filteredCharts
        {
            List<string> difficultyLabels = new List<string>();
            if (c.Difficulties != null && c.Difficulties.Count > 0)
            {
                foreach (var d in c.Difficulties)
                {
                    if (!string.IsNullOrEmpty(d))
                    {
                        var splitted = d.Split(new char[] { ',', '，', ' ', '/', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        difficultyLabels.AddRange(splitted.Where(x => !string.IsNullOrWhiteSpace(x)));
                    }
                }
            }
            
            if (difficultyLabels.Count == 0)
            {
                var urlDecoded = System.Net.WebUtility.UrlDecode(c.DownloadUrl);
                var match = System.Text.RegularExpressions.Regex.Match(
                    urlDecoded,
                    @"\[Lv\.(.*?)\]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                difficultyLabels = ExtractDifficultyLabels(match);
            }

            Charts.Add(new MdmcChart
            {
                Id = c.Id,
                Title = c.Title,
                Artist = c.Artist,
                Charter = c.Author,
                Bpm = NormalizeBpm(c.Bpm),
                CustomCoverUrl = ResolveResourceUrl(c.CoverUrl),
                CustomDownloadUrl = ResolveResourceUrl(c.DownloadUrl),
                CustomDemoUrl = ResolveResourceUrl(c.DemoUrl),
                CustomDemoMp3Url = ResolveResourceUrl(c.DemoMp3Url),
                Sheets = difficultyLabels
                    .Select(label => new MdmcSheet { Difficulty = label })
                    .ToList(),
                SearchText = searchText // 传递当前搜索关键词用于高亮
            });

            Log($"Chart view item: title='{c.Title}', cover='{c.CoverUrl}', demo='{c.DemoUrl}', mp3='{c.DemoMp3Url}', download='{c.DownloadUrl}'");
        }

        IsEmpty = Charts.Count == 0;
        IsLoading = false;
        
        _chartCountText = $"当前页面 {Charts.Count} 张谱面";
        UpdateStatusMessage();

        Log($"Album detail initialized. Visible charts: {Charts.Count}");
        _ = LoadCoversAsync();
    }

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (Charts.Count == 0)
            return;

        if (!IsDotNet6Installed())
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow as MainWindow
                : null;

            if (mainWindow != null)
            {
                await mainWindow.ShowMessageBoxAsync("请先在Mod列表顶部下载.net6运行环境！");
            }

            return;
        }

        var queued = 0;
        foreach (var chart in Charts)
        {
            var url = chart.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            // 特例：修复调色盘等特殊路径在批量下载时的镜像识别问题
            if (url.Contains("~%23FFFFFF~") || url.Contains("~#FFFFFF~") || (chart.Title?.Contains("调色盘") == true))
            {
                var manualUrl = url.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                url = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
                chart.CustomDownloadUrl = url;
            }

            _downloadManagerService.EnqueueDownload(chart);
            queued++;
        }

        if (queued > 0)
        {
            _notificationService.ShowSuccess($"已添加当前整合包的 {queued} 张谱面到下载列表");
            Log($"Queued {queued} charts for category '{Category?.Name}'.");
        }
    }

    private bool IsDotNet6Installed()
    {
        var gamePath = _configService.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath))
            return false;

        var net6Path = System.IO.Path.Combine(gamePath, "MelonLoader", "net6", "MelonLoader.dll");
        return System.IO.File.Exists(net6Path);
    }

    private static List<string> ExtractDifficultyLabels(Match match)
    {
        if (!match.Success)
            return ["?"];

        var raw = match.Groups[1].Value;
        var labels = raw
            .Split([',', '，', ' ', '/', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToList();

        return labels.Count > 0 ? labels : ["?"];
    }

    private static string NormalizeBpm(string? bpm)
    {
        if (string.IsNullOrWhiteSpace(bpm))
            return string.Empty;

        return Regex.Replace(bpm.Trim(), @"\d+(\.\d+)?", match =>
        {
            return decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value.ToString("G29", CultureInfo.InvariantCulture)
                : match.Value;
        });
    }

    private string ResolveResourceUrl(string? url)
    {
        return GitHubMirrorHelper.ApplyMirror(url, _configService.Config.DownloadSource);
    }

    private async Task LoadCoversAsync()
    {
        // 使用优化客户端以享受高速 DNS 竞速（如果开启了的话）
        using var http = MdModManager.Helpers.HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Remove("User-Agent");
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");

        var tasks = new List<Task>();

        foreach (var chart in Charts)
        {
            if (string.IsNullOrWhiteSpace(chart.CoverUrl))
            {
                Log($"Skip cover load for '{chart.Title}': empty url");
                continue;
            }

            if (chart.CoverImage != null) continue;

            tasks.Add(Task.Run(async () =>
            {
                await _coverSemaphore.WaitAsync();
                try
                {
                    if (chart.CoverImage != null) return;

                    var fetchUrl = chart.CoverUrl;
                    // 特例处理：调色盘谱面因特殊符号 # (%23) 在重定向时易断链，此处强制进行特例修正
                    if (!string.IsNullOrEmpty(fetchUrl) && (fetchUrl.Contains("~%23FFFFFF~") || fetchUrl.Contains("~#FFFFFF~") || (chart.Title?.Contains("调色盘") == true)))
                    {
                        var manualUrl = fetchUrl.Replace("/blob/", "/").Replace("github.com", "raw.githubusercontent.com").Replace("~#FFFFFF~", "~%23FFFFFF~");
                        fetchUrl = GitHubMirrorHelper.ApplyMirror(manualUrl, _configService.Config.DownloadSource);
                    }

                    Log($"Loading cover for '{chart.Title}': {fetchUrl}");
                    var bytes = await http.GetByteArrayAsync(fetchUrl);
                    using var ms = new System.IO.MemoryStream(bytes);
                    var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => chart.CoverImage = bmp);
                    Log($"Cover loaded for '{chart.Title}', bytes={bytes.Length}");
                }
                catch (Exception ex)
                {
                    Log($"Cover load failed for '{chart.Title}': {ex.Message}");
                }
                finally
                {
                    _coverSemaphore.Release();
                }
            }));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            Ioc.Default.GetRequiredService<ChartDownloadViewModel>().StopPlayback();
            var collectionVm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            mainVm.CurrentPage = collectionVm;
        }

        await Task.CompletedTask;
    }

    private static void Log(string message)
    {
        RuntimeLog.Write("AlbumDetailViewModel", message);
    }
}
