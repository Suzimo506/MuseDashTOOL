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

        // 监听 ViewModel 的属性变化，处理平滑滚动请求
        this.DataContextChanged += (s, e) =>
        {
            if (DataContext is ChartDownloadViewModel vm)
            {
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(ChartDownloadViewModel.RequestedScrollY) 
                        && vm.RequestedScrollY.HasValue)
                    {
                        var y = vm.RequestedScrollY.Value;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (scrollViewer != null)
                            {
                                scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, y);
                            }
                        });
                        // 重置请求，防止重复触发
                        vm.RequestedScrollY = null;
                    }
                    if (args.PropertyName == nameof(ChartDownloadViewModel.IsEditingPageNumber) 
                        && vm.IsEditingPageNumber)
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
                };
            }
        };
    }

    /// <summary>处理滚动事件 – 到底部时自动加载更多</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer && DataContext is ChartDownloadViewModel vm)
        {
            // 传给 VM 进行内存管理
            vm.UpdateScrollPosition(scrollViewer.Offset.Y);

            // 翻页模式下不再自动触发加载更多
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

    private void OnPageNumberClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is ChartDownloadViewModel vm)
        {
            vm.StartEditPageCommand.Execute(null);
        }
    }

    private void OnPageJumpLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ChartDownloadViewModel vm)
        {
            vm.CancelEditPageCommand.Execute(null);
        }
    }
}
