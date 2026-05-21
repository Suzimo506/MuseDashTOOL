using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class EuterpeView : UserControl
{
    public EuterpeView()
    {
        InitializeComponent();
        
        var scrollViewer = this.FindControl<ScrollViewer>("ChartScrollViewer");

        // 监听数据上下文属性变化以自动复位滚动
        this.DataContextChanged += (s, e) =>
        {
            if (DataContext is EuterpeViewModel vm)
            {
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(EuterpeViewModel.RequestedScrollY) 
                        && vm.RequestedScrollY.HasValue)
                    {
                        var y = vm.RequestedScrollY.Value;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (scrollViewer != null)
                            {
                                scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, y);
                            }
                        }, Avalonia.Threading.DispatcherPriority.Background);
                        vm.RequestedScrollY = null;
                    }
                    if (args.PropertyName == nameof(EuterpeViewModel.CurrentPage))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (scrollViewer != null)
                            {
                                scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, 0);
                            }
                        }, Avalonia.Threading.DispatcherPriority.Background);
                    }
                    // 监控页码编辑状态以自动聚焦并全选
                    if (args.PropertyName == nameof(EuterpeViewModel.IsEditingPageNumber) 
                        && vm.IsEditingPageNumber)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var tb = this.FindControl<TextBox>("PageJumpTextBox_Top")
                                ?? this.FindControl<TextBox>("PageJumpTextBox_Bottom");
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

    // 失去焦点时执行搜索过滤
    private async void OnSearchBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EuterpeViewModel vm)
        {
            await vm.ApplySearchAsync();
        }
    }

    // 按下回车键时触发搜索
    private async void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is EuterpeViewModel vm)
        {
            e.Handled = true;
            await vm.ApplySearchAsync();
        }
    }

    // 点击页码数字启动编辑
    private void OnPageNumberClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is EuterpeViewModel vm)
        {
            vm.StartEditPageCommand.Execute(null);
        }
    }

    // 页码输入框失去焦点执行跳转
    private void OnPageJumpLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EuterpeViewModel vm)
        {
            vm.JumpPageCommand.Execute(null);
        }
    }

    // 处理页码输入框键盘导航
    private void OnPageJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not EuterpeViewModel vm)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.JumpPageCommand.Execute(null);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelEditPageCommand.Execute(null);
        }
    }

    // 点击背景处清空输入框焦点
    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.FocusManager?.ClearFocus();
    }
}
