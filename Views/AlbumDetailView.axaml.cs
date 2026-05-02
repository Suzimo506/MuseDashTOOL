using Avalonia.Controls;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class AlbumDetailView : UserControl
{
    private AlbumDetailViewModel? _currentVm;

    public AlbumDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_currentVm != null)
            _currentVm.PropertyChanged -= OnVmPropertyChanged;

        _currentVm = DataContext as AlbumDetailViewModel;
        if (_currentVm != null)
            _currentVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumDetailViewModel.RequestedScrollY))
        {
            var sv = this.FindControl<ScrollViewer>("ChartScrollViewer");
            if (sv != null)
                sv.Offset = new Avalonia.Vector(0, 0);
        }
    }

    private void OnBackgroundPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.FocusManager?.ClearFocus();
    }

    private void OnSearchIconPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Avalonia.Visual);
        if (point.Properties.IsRightButtonPressed)
        {
            if (DataContext is AlbumDetailViewModel vm)
            {
                vm.ClearSearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OnPageNumberClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is AlbumDetailViewModel vm)
        {
            vm.StartEditPageCommand.Execute(null);
        }
    }

    private void OnPageJumpLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AlbumDetailViewModel vm)
        {
            vm.JumpPageCommand.Execute(null);
        }
    }
}
