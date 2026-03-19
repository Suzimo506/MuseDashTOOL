using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class CommunityCategoryDetailViewModel : ObservableObject
{
    public static readonly string[] DateOptions =
    [
        "2023-11",
        "2024-6",
        "2025-1",
        "2025-8"
    ];

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private string _selectedDate = DateOptions[0];

    [ObservableProperty]
    private ObservableCollection<string> _currentEntries = new();

    [ObservableProperty]
    private string _lanzouUrl = string.Empty;

    [ObservableProperty]
    private string _lanzouPassword = string.Empty;

    public bool HasLanzouLink => !string.IsNullOrEmpty(LanzouUrl);
    public string LanzouTooltip => $"点击打开链接并自动复制密码: {LanzouPassword}";

    public bool IsDate202311Selected => SelectedDate == DateOptions[0];
    public bool IsDate202406Selected => SelectedDate == DateOptions[1];
    public bool IsDate202501Selected => SelectedDate == DateOptions[2];
    public bool IsDate202508Selected => SelectedDate == DateOptions[3];
    public bool HasEntries => CurrentEntries.Count > 0;
    public bool IsEmpty => !HasEntries;
    public string EmptyMessage => $"{SelectedDate} 的列表内容待填充";

    public Task InitializeAsync(string categoryName)
    {
        CategoryName = categoryName;
        SelectedDate = DateOptions[0];
        RefreshEntries();
        return Task.CompletedTask;
    }

    partial void OnSelectedDateChanged(string value)
    {
        RefreshEntries();
        OnPropertyChanged(nameof(IsDate202311Selected));
        OnPropertyChanged(nameof(IsDate202406Selected));
        OnPropertyChanged(nameof(IsDate202501Selected));
        OnPropertyChanged(nameof(IsDate202508Selected));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void RefreshEntries()
    {
        CurrentEntries.Clear();

        try
        {
            var basePath = Path.Combine(AppContext.BaseDirectory, "SongRepository", CategoryName, SelectedDate);
            var filePath = Path.Combine(basePath, "list.txt");

            // Fallback for development/different environments
            if (!File.Exists(filePath))
            {
                var devPath = Path.Combine(Environment.CurrentDirectory, "SongRepository", CategoryName, SelectedDate, "list.txt");
                if (File.Exists(devPath))
                    filePath = devPath;
            }

            if (File.Exists(filePath))
            {
                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        CurrentEntries.Add(line.Trim());
                }
            }
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Write("CommunityCategoryDetailVM", $"Failed to load entries: {ex.Message}");
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        
        LoadLanzouLink();
    }

    private void LoadLanzouLink()
    {
        LanzouUrl = string.Empty;
        LanzouPassword = string.Empty;

        try
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "SongRepository", "community_links.json");
            if (!File.Exists(filePath))
            {
                var devPath = Path.Combine(Environment.CurrentDirectory, "SongRepository", "community_links.json");
                if (File.Exists(devPath)) filePath = devPath;
            }

            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CommunityLink>>>(json);

                if (data != null && data.TryGetValue(CategoryName, out var categoryData) &&
                    categoryData.TryGetValue(SelectedDate, out var linkInfo))
                {
                    LanzouUrl = linkInfo.Url;
                    LanzouPassword = linkInfo.Password;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("CommunityCategoryDetailVM", $"Failed to load link data: {ex.Message}");
        }

        OnPropertyChanged(nameof(HasLanzouLink));
        OnPropertyChanged(nameof(LanzouTooltip));
    }

    [RelayCommand]
    private async Task OpenLanzouAsync()
    {
        if (string.IsNullOrEmpty(LanzouUrl)) return;

        try
        {
            // Open browser
            Process.Start(new ProcessStartInfo(LanzouUrl) { UseShellExecute = true });

            // Copy password to clipboard
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(LanzouPassword);
                
                // Show notification if service available
                var notify = Ioc.Default.GetService<MdModManager.Services.INotificationService>();
                notify?.ShowSuccess("密码已复制到粘贴板");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("CommunityCategoryDetailVM", $"Failed to open link or copy password: {ex.Message}");
        }
    }

    public class CommunityLink
    {
        public string Url { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    [RelayCommand]
    private void SelectDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
            return;

        if (SelectedDate == date)
        {
            RefreshEntries();
            return;
        }

        SelectedDate = date;
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            var vm = Ioc.Default.GetRequiredService<AlbumCollectionViewModel>();
            mainVm.CurrentPage = vm;
        }

        await Task.CompletedTask;
    }
}
