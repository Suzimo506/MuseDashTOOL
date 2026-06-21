using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdModManager.ViewModels;
using System.ComponentModel;

namespace MdModManager.Views;

public partial class GlobalChartSearchView : UserControl
{
    private GlobalChartSearchViewModel? _currentVm;

    public GlobalChartSearchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_currentVm != null)
            _currentVm.PropertyChanged -= OnVmPropertyChanged;

        _currentVm = DataContext as GlobalChartSearchViewModel;
        if (_currentVm != null)
            _currentVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_currentVm == null)
            return;

        if (e.PropertyName == nameof(GlobalChartSearchViewModel.RequestedScrollY) ||
            e.PropertyName == nameof(GlobalChartSearchViewModel.CurrentPage))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var sv = this.FindControl<ScrollViewer>("ResultScrollViewer");
                if (sv != null)
                    sv.Offset = new Avalonia.Vector(sv.Offset.X, 0);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        if (e.PropertyName == nameof(GlobalChartSearchViewModel.IsEditingPageNumber) && _currentVm.IsEditingPageNumber)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var tb = this.FindControl<TextBox>("PageJumpTextBox");
                if (tb != null)
                {
                    tb.Focus();
                    tb.SelectAll();
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private async void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is GlobalChartSearchViewModel vm)
        {
            e.Handled = true;
            await vm.SearchCommand.ExecuteAsync(null);
        }
    }

    private void OnPageNumberClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GlobalChartSearchViewModel vm)
            vm.StartEditPageCommand.Execute(null);
    }

    private void OnPageJumpLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GlobalChartSearchViewModel vm)
            vm.JumpPageCommand.Execute(null);
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.FocusManager?.ClearFocus();
    }
}
