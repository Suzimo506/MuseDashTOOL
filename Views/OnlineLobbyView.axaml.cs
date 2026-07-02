using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;

namespace MdModManager.Views;

public partial class OnlineLobbyView : UserControl
{
    public OnlineLobbyView()
    {
        InitializeComponent();
    }

    private void OnNodeScrollWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;

        var maxOffset = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        if (maxOffset <= 0) return;

        var nextOffset = Math.Clamp(scrollViewer.Offset.X - e.Delta.Y * 40, 0, maxOffset);
        scrollViewer.Offset = new Vector(nextOffset, scrollViewer.Offset.Y);
        e.Handled = true;
    }
}
