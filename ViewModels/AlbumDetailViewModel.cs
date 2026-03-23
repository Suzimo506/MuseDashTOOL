using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public partial class AlbumDetailViewModel : ObservableObject
{
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
    }

    public async Task InitializeAsync(DesignerCategory category)
    {
        Log($"Initializing album detail for '{category.Name}'.");
        Category = category;
        Charts.Clear();
        IsLoading = true;
        IsEmpty = false;

        var charts = await _collectionService.GetChartsAsync(category.Name);
        Category.Charts = charts;
        Log($"Category '{category.Name}' returned {charts.Count} chart records.");

        foreach (var c in charts)
        {
            var urlDecoded = System.Net.WebUtility.UrlDecode(c.DownloadUrl);
            var match = System.Text.RegularExpressions.Regex.Match(
                urlDecoded,
                @"\[Lv\.(.*?)\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var difficultyLabels = ExtractDifficultyLabels(match);

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
                    .ToList()
            });

            Log($"Chart view item: title='{c.Title}', cover='{c.CoverUrl}', demo='{c.DemoUrl}', mp3='{c.DemoMp3Url}', download='{c.DownloadUrl}'");
        }

        IsEmpty = Charts.Count == 0;
        IsLoading = false;
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
            if (string.IsNullOrWhiteSpace(chart.DownloadUrl))
                continue;

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
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
                ? value.ToString("0.000", CultureInfo.InvariantCulture)
                : match.Value;
        });
    }

    private string ResolveResourceUrl(string? url)
    {
        return GitHubMirrorHelper.ApplyMirror(url, _configService.Config.DownloadSource);
    }

    private async Task LoadCoversAsync()
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");

        foreach (var chart in Charts)
        {
            if (string.IsNullOrWhiteSpace(chart.CoverUrl))
            {
                Log($"Skip cover load for '{chart.Title}': empty url");
                continue;
            }

            try
            {
                Log($"Loading cover for '{chart.Title}': {chart.CoverUrl}");
                var bytes = await http.GetByteArrayAsync(chart.CoverUrl);
                using var ms = new System.IO.MemoryStream(bytes);
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => chart.CoverImage = bmp);
                Log($"Cover loaded for '{chart.Title}', bytes={bytes.Length}");
            }
            catch (Exception ex)
            {
                Log($"Cover load failed for '{chart.Title}': {ex.Message}");
            }
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
