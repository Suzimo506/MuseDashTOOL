using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class AlbumDetailViewModel : ObservableObject
{
    private const string ProxyPrefix = "https://ghproxy.net/";
    private readonly ChartDownloadViewModel _chartDownloadViewModel;
    private readonly IAlbumCollectionService _collectionService;

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
        IAlbumCollectionService collectionService)
    {
        _chartDownloadViewModel = chartDownloadViewModel;
        _collectionService = collectionService;
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
            string levelText = "?";
            var urlDecoded = System.Net.WebUtility.UrlDecode(c.DownloadUrl);
            var match = System.Text.RegularExpressions.Regex.Match(
                urlDecoded,
                @"\[Lv\.(.*?)\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
                levelText = match.Groups[1].Value;

            Charts.Add(new MdmcChart
            {
                Id = c.Id,
                Title = c.Title,
                Artist = c.Artist,
                Charter = c.Author,
                CustomCoverUrl = ToAccessibleUrl(c.CoverUrl),
                CustomDownloadUrl = ToAccessibleUrl(c.DownloadUrl),
                CustomDemoUrl = ToAccessibleUrl(c.DemoUrl),
                CustomDemoMp3Url = ToAccessibleUrl(c.DemoMp3Url),
                Sheets = new List<MdmcSheet> { new MdmcSheet { Difficulty = levelText } }
            });

            Log($"Chart view item: title='{c.Title}', cover='{c.CoverUrl}', demo='{c.DemoUrl}', mp3='{c.DemoMp3Url}', download='{c.DownloadUrl}'");
        }

        IsEmpty = Charts.Count == 0;
        IsLoading = false;
        Log($"Album detail initialized. Visible charts: {Charts.Count}");
        _ = LoadCoversAsync();
    }

    private static string ToAccessibleUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (url.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyPrefix + url;
        }

        return url;
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
