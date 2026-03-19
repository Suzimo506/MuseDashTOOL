using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class ChartDownloadView : UserControl
{
    public ChartDownloadView()
    {
        InitializeComponent();
        
        var scrollViewer = this.FindControl<ScrollViewer>("ChartScrollViewer");
        if (scrollViewer != null)
        {
            scrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    /// <summary>处理滚动事件 – 到底部时自动加载更多</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer && DataContext is ChartDownloadViewModel vm)
        {
            // 传给 VM 进行内存管理
            vm.UpdateScrollPosition(scrollViewer.Offset.Y);

            // 如果垂直偏移量 + 视口高度 接近 总体高度（例如在 200 像素内），则触发加载
            var threshold = 200;
            if (scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - threshold)
            {
                if (vm.LoadNextPageCommand.CanExecute(null))
                {
                    vm.LoadNextPageCommand.Execute(null);
                }
            }
        }
    }

    /// <summary>处理排序按钮点击 – 根据 Tag 更新 ViewModel 的 SelectedSortIndex</summary>
    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn
            && btn.Tag is string tagStr
            && int.TryParse(tagStr, out var idx)
            && DataContext is ChartDownloadViewModel vm)
        {
            if (vm.SelectedSortIndex == idx)
            {
                vm.RefreshCommand.Execute(null);
            }
            else
            {
                vm.SelectedSortIndex = idx;
            }
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
            if (DataContext is ChartDownloadViewModel vm)
            {
                vm.ClearSearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
