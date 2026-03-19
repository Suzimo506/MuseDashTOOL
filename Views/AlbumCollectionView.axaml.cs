using Avalonia.Controls;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class AlbumCollectionView : UserControl
{
    private bool _canSave = false;

    public AlbumCollectionView()
    {
        InitializeComponent();
        
        var scrollViewer = this.FindControl<ScrollViewer>("AlbumScrollViewer");
        if (scrollViewer != null)
        {
            // 保存滚动位置到 VM
            scrollViewer.ScrollChanged += (s, e) =>
            {
                // 只有在已加载且未在卸载过程时才保存
                if (_canSave && DataContext is AlbumCollectionViewModel vm)
                {
                    if (scrollViewer.Viewport.Height > 0)
                    {
                        vm.ScrollOffset = scrollViewer.Offset.Y;
                    }
                }
            };

            this.Loaded += (s, e) =>
            {
                _canSave = true;
                
                // 恢复位置（仅在当前 View 的 Offset 与 VM 中保存的不一致时执行，例如首次创建或被重置时）
                if (DataContext is AlbumCollectionViewModel vm && 
                    vm.ScrollOffset > 0 && 
                    Math.Abs(scrollViewer.Offset.Y - vm.ScrollOffset) > 1)
                {
                    // 确保在布局完成后设置 Offset
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        scrollViewer.Offset = new Avalonia.Vector(scrollViewer.Offset.X, vm.ScrollOffset);
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
                }
            };

            this.Unloaded += (s, e) =>
            {
                _canSave = false;
            };
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _canSave = false;
    }
}
