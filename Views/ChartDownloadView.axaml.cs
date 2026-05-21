using Avalonia.Controls;
using Avalonia.Input;
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

        // 监听属性变化处理滚动
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
                        }, Avalonia.Threading.DispatcherPriority.Background); // 在布局完成后执行
                        // 重置请求防止重复
                        vm.RequestedScrollY = null;
                    }
                    if (args.PropertyName == nameof(ChartDownloadViewModel.CurrentPage))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (scrollViewer != null)
                            {
                                scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, 0); // 翻页后重置滚动位置
                            }
                        }, Avalonia.Threading.DispatcherPriority.Background);
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

    // 处理滚动事件
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer && DataContext is ChartDownloadViewModel vm)
        {
            // 内存管理
            vm.UpdateScrollPosition(scrollViewer.Offset.Y);

            // 翻页不加载更多
        }
    }

    // 处理排序按钮点击
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

    private async void OnSearchBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChartDownloadViewModel vm)
        {
            await vm.ApplySearchAsync();
        }
    }

    private async void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChartDownloadViewModel vm)
        {
            e.Handled = true;
            await vm.ApplySearchAsync();
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
            // 失去焦点直接跳转
            vm.JumpPageCommand.Execute(null);
        }
    }
}
