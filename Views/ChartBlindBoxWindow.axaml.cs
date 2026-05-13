using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class ChartBlindBoxWindow : Window
{
    private ChartBlindBoxViewModel? _vm;

    public ChartBlindBoxWindow()
    {
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task ShowBlindBoxAsync(Window? owner)
    {
        _vm = new ChartBlindBoxViewModel();
        DataContext = _vm;

        this.ShowInTaskbar = true;
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        this.Closed += (s, e) =>
        {
            _vm?.Cleanup();
            _vm?.Dispose();
            _vm = null;
            tcs.TrySetResult(true);
        };

        // 半模态: 禁用父窗口交互
        Avalonia.Controls.Control? contentControl = owner?.Content as Avalonia.Controls.Control;
        bool originalHitTest = true;
        if (contentControl != null)
        {
            originalHitTest = contentControl.IsHitTestVisible;
            contentControl.IsHitTestVisible = false;
        }

        this.Show();
        await _vm.InitializeAsync();
        await tcs.Task;

        if (owner != null)
        {
            if (contentControl != null) contentControl.IsHitTestVisible = originalHitTest;
            if (owner.WindowState != WindowState.Minimized) owner.Activate();
        }
    }

    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out var idx) && _vm != null)
        {
            _vm.SelectedFilterIndex = idx;
        }
    }
}
