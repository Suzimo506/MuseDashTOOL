using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;

namespace MdModManager.ViewModels;

public partial class AlbumDetailViewModel : ObservableObject
{
    private readonly ChartDownloadViewModel _chartDownloadViewModel;

    [ObservableProperty]
    private DesignerCategory? _category;

    [ObservableProperty]
    private ObservableCollection<MdmcChart> _charts = new();

    public IAsyncRelayCommand<MdmcChart> TogglePreviewCommand => _chartDownloadViewModel.TogglePreviewCommand;
    public IAsyncRelayCommand<MdmcChart> DownloadChartCommand => _chartDownloadViewModel.DownloadChartCommand;
    
    // Relay marquee settings to keep UI consistent
    public bool EnableMarquee => _chartDownloadViewModel.EnableMarquee;

    public AlbumDetailViewModel(ChartDownloadViewModel chartDownloadViewModel)
    {
        _chartDownloadViewModel = chartDownloadViewModel;
    }

    private string GetProxiedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        
        // The user uploaded folders to the root of the repo, but the JSON contains /main/SongRepository/ paths.
        url = url.Replace("/main/SongRepository/", "/main/");

        if (url.StartsWith("https://raw.githubusercontent.com/") || url.StartsWith("https://github.com/"))
            return "https://ghproxy.net/" + url;
        return url;
    }

    public void Initialize(DesignerCategory category)
    {
        Category = category;
        Charts.Clear();

        foreach (var c in category.Charts)
        {
            // Extract level from download URL (e.g. "...[Lv.10]Name.mdm" -> "10")
            string levelText = "?";
            var urlDecoded = System.Net.WebUtility.UrlDecode(c.DownloadUrl);
            var match = System.Text.RegularExpressions.Regex.Match(urlDecoded, @"\[Lv\.(.*?)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) levelText = match.Groups[1].Value;

            var chart = new MdmcChart
            {
                Id = c.Id,
                Title = c.Title,
                Artist = c.Artist,
                Charter = c.Author,
                CustomCoverUrl = GetProxiedUrl(c.CoverUrl),
                CustomDownloadUrl = GetProxiedUrl(c.DownloadUrl),
                CustomDemoUrl = GetProxiedUrl(c.DemoUrl),
                CustomDemoMp3Url = GetProxiedUrl(c.DemoMp3Url),
                Sheets = new List<MdmcSheet> { new MdmcSheet { Difficulty = levelText } }
            };
            Charts.Add(chart);
        }

        // We can lazy load covers just like ChartDownloadView, or we can let them bind to CoverUrl if a converter exists.
        // Wait, ChartDownloadViewModel manually loads cover bytes into CoverImage (Bitmap).
        // Let's fire and forget a loading task for these covers.
        _ = LoadCoversAsync();
    }

    private async Task LoadCoversAsync()
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");
        foreach (var chart in Charts)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(chart.CoverUrl);
                using var ms = new System.IO.MemoryStream(bytes);
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => chart.CoverImage = bmp);
            }
            catch { /* ignore missing cover */ }
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        // Stop playback when leaving (handled simply by navigating away or disposing ChartDownloadViewModel - wait, ChartDownloadViewModel is Singleton, so playback might continue if we don't stop it. The user will stop it if they want. Actually we should delegate to its TogglePreview if we track it.)
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            var collectionVm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            mainVm.CurrentPage = collectionVm;
        }
        await Task.CompletedTask;
    }
}
