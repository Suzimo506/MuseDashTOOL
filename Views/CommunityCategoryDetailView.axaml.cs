using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.ViewModels;
using System.ComponentModel;

namespace MdModManager.Views;

public partial class CommunityCategoryDetailView : UserControl
{
    private CommunityCategoryDetailViewModel? _currentVm;

    public CommunityCategoryDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_currentVm != null)
            _currentVm.PropertyChanged -= OnVmPropertyChanged;

        _currentVm = DataContext as CommunityCategoryDetailViewModel;
        if (_currentVm != null)
            _currentVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommunityCategoryDetailViewModel.RequestedScrollY))
        {
            var sv = this.FindControl<ScrollViewer>("ChartScrollViewer");
            if (sv != null)
                sv.Offset = new Avalonia.Vector(0, 0);
        }
    }

    /// <summary>处理排序按钮点击 – 根据 Tag 更新 ViewModel 的 SelectedSortIndex</summary>
    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn
            && btn.Tag is string tagStr
            && int.TryParse(tagStr, out var idx)
            && DataContext is CommunityCategoryDetailViewModel vm)
        {
            vm.SelectedSortIndex = idx;
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
            if (DataContext is CommunityCategoryDetailViewModel vm)
            {
                vm.ClearSearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OnPageNumberClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is CommunityCategoryDetailViewModel vm)
        {
            vm.StartEditPageCommand.Execute(null);
        }
    }

    private void OnPageJumpLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CommunityCategoryDetailViewModel vm)
        {
            vm.JumpPageCommand.Execute(null);
        }
    }
}
