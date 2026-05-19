using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class AccountView : UserControl
{
    private AccountViewModel? _currentVm;

    public AccountView()
    {
        InitializeComponent();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        // 清理旧订阅，防止多次触发或内存泄漏
        if (_currentVm != null)
            _currentVm.ScrollToItemRequested -= OnScrollToItem;

        _currentVm = DataContext as AccountViewModel;

        if (_currentVm != null)
            _currentVm.ScrollToItemRequested += OnScrollToItem;
    }

    // 搜索框回车触发搜索
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is AccountViewModel vm)
        {
            vm.SearchConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }

    // 搜索框失去焦点时自动触发搜索
    private void OnSearchBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel vm && !string.IsNullOrWhiteSpace(vm.SearchText))
        {
            vm.SearchConfirmCommand.Execute(null);
        }
    }

    // 搜索按钮右键清空搜索
    private void OnSearchButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Control);
        if (point.Properties.IsRightButtonPressed)
        {
            if (DataContext is AccountViewModel vm)
            {
                vm.SearchText = "";
                e.Handled = true;
            }
        }
    }

    // 滚动到指定项
    private void OnScrollToItem(int itemIndex)
    {
        // 使用 Background 优先级确保在渲染之后执行，此时容器已生成
        Dispatcher.UIThread.Post(() =>
        {
            var itemsControl = this.FindControl<ItemsControl>("RecordsItemsControl");
            if (itemsControl == null) return;

            // 获取对应索引的 UI 容器
            var container = itemsControl.ContainerFromIndex(itemIndex);
            if (container != null)
            {
                // 让该容器滚动到可视区域
                container.BringIntoView();
            }
            else
            {
                // 如果容器尚未生成（理论上非虚化列表不应出现），则使用估算值兜底
                var scroller = this.FindControl<ScrollViewer>("RecordScroller");
                if (scroller != null)
                {
                    double estimatedOffset = itemIndex * 90.0;
                    double maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                    scroller.Offset = new Avalonia.Vector(0, Math.Min(estimatedOffset, maxOffset));
                }
            }
        }, DispatcherPriority.Background);
    }

    // Called by the ScrollViewer via x:Name reference in XAML
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not AccountViewModel vm) return;

        // 当滚动到距底部 200px 以内时，触发加载更多
        double remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 200)
        {
            vm.LoadMore();
        }
    }
}
