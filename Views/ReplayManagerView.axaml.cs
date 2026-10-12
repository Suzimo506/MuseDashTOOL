using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MdModManager.Models;
using MdModManager.Services;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class ReplayManagerView : UserControl
{
    private ReplayManagerViewModel? _viewModel;

    public ReplayManagerView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DataContextChanged += (_, _) => BindViewModel();
        AttachedToVisualTree += (_, _) => BindViewModel();
        DetachedFromVisualTree += (_, _) => UnbindViewModel();
    }

    private void BindViewModel()
    {
        UnbindViewModel();
        _viewModel = DataContext as ReplayManagerViewModel;
        if (_viewModel != null) _viewModel.PageChanged += ResetScroll;
    }

    private void UnbindViewModel()
    {
        if (_viewModel != null) _viewModel.PageChanged -= ResetScroll;
        _viewModel = null;
    }

    private void ResetScroll(object? sender, EventArgs e)
    {
        var viewModel = _viewModel;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (viewModel != null && ReferenceEquals(_viewModel, viewModel))
                this.FindControl<ScrollViewer>("ReplayScroll")!.Offset = default;
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        e.DragEffects = DataContext is ReplayManagerViewModel { IsBusy: false } && files?.Any(f =>
            f.TryGetLocalPath()?.EndsWith(".mdrp", StringComparison.OrdinalIgnoreCase) == true) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        e.Handled = true;
        if (files == null || DataContext is not ReplayManagerViewModel vm) return;
        await vm.ImportAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(p => p.EndsWith(".mdrp", StringComparison.OrdinalIgnoreCase)));
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReplayManagerViewModel { IsBusy: false } vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = I18nService.Instance["Replay_Import"],
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("BetterMD Replay") { Patterns = new[] { "*.mdrp" } } }
        });
        await vm.ImportAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReplayInfo replay } || DataContext is not ReplayManagerViewModel { IsBusy: false } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await MessageBox.ShowDialogAsync(owner,
            string.Format(I18nService.Instance["Replay_DeleteAsk"], replay.Title), true);
        if (confirmed) await vm.DeleteAsync(replay);
    }
}
