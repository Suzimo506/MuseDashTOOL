using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class CommunityCategoryDetailView : UserControl
{
    public CommunityCategoryDetailView()
    {
        InitializeComponent();
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
