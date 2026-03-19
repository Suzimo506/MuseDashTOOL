using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Models;
using MdModManager.Services;

namespace MdModManager.ViewModels;

public partial class DesignerCategoryItemViewModel : ObservableObject
{
    [ObservableProperty]
    private DesignerCategory _category;

    [ObservableProperty]
    private Bitmap? _coverImage;

    public DesignerCategoryItemViewModel(DesignerCategory category)
    {
        _category = category;
    }

    public void LoadCoverImage()
    {
        var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Pictures");
        if (!Directory.Exists(basePath)) return;

        var extensions = new[] { ".png", ".jpg", ".jpeg" };
        foreach (var ext in extensions)
        {
            var filePath = Path.Combine(basePath, $"{_category.Name}{ext}");
            if (File.Exists(filePath))
            {
                try
                {
                    using var stream = File.OpenRead(filePath);
                    CoverImage = new Bitmap(stream);
                    break;
                }
                catch
                {
                    // Ignore image load errors
                }
            }
        }
    }
}

public partial class AlbumCollectionViewModel : ObservableObject
{
    private readonly IAlbumCollectionService _collectionService;

    [ObservableProperty]
    private ObservableCollection<DesignerCategoryItemViewModel> _categories = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    public AlbumCollectionViewModel(IAlbumCollectionService collectionService)
    {
        _collectionService = collectionService;
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        Categories.Clear();

        var collections = await _collectionService.GetCollectionsAsync();
        
        foreach (var category in collections)
        {
            var item = new DesignerCategoryItemViewModel(category);
            item.LoadCoverImage();
            Categories.Add(item);
        }

        IsEmpty = !Categories.Any();
        IsLoading = false;
    }

    [RelayCommand]
    private async Task OpenCategoryAsync(DesignerCategoryItemViewModel item)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            var detailVm = Ioc.Default.GetRequiredService<AlbumDetailViewModel>();
            mainVm.CurrentPage = detailVm;
            await detailVm.InitializeAsync(item.Category);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            var chartVm = Ioc.Default.GetRequiredService<ChartDownloadViewModel>();
            mainVm.CurrentPage = chartVm;
        }
        await Task.CompletedTask;
    }
}
