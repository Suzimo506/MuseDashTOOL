using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;

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
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
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
