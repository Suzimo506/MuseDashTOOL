using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MdModManager.ViewModels;

namespace MdModManager.Views;

public partial class AlbumCollectionView : UserControl
{
    private bool _canSave = false;

    public AlbumCollectionView()
    {
        InitializeComponent();
        
        this.DataContextChanged += (s, e) =>
        {
            if (DataContext is AlbumCollectionViewModel vm)
            {
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(AlbumCollectionViewModel.RequestedSearchScrollY) 
                        && vm.RequestedSearchScrollY.HasValue)
                    {
                        var requestedScrollY = vm.RequestedSearchScrollY.Value;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var sv = this.FindControl<ScrollViewer>("AlbumScrollViewer");
                            if (sv != null)
                            {
                                var searchAnchor = this.FindControl<Control>("SearchResultsAnchor");
                                var y = searchAnchor != null && vm.HasSearchResults
                                    ? Math.Max(0, searchAnchor.Bounds.Y)
                                    : requestedScrollY;
                                sv.Offset = new Avalonia.Vector(sv.Offset.X, y);
                            }
                        }, Avalonia.Threading.DispatcherPriority.Background);
                        vm.RequestedSearchScrollY = null;
                    }

                    if (args.PropertyName == nameof(AlbumCollectionViewModel.IsEditingPageNumber) 
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

                    _ = LoadVisibleCategoryCoversAsync(scrollViewer, vm);
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

                if (DataContext is AlbumCollectionViewModel loadedVm)
                {
                    _ = LoadVisibleCategoryCoversAsync(scrollViewer, loadedVm);
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

    private void OnBackgroundPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.FocusManager?.ClearFocus();
    }

    private void OnPageNumberClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is AlbumCollectionViewModel vm)
        {
            vm.StartEditPageCommand.Execute(null);
        }
    }

    private void OnPageJumpLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AlbumCollectionViewModel vm)
        {
            vm.JumpPageCommand.Execute(null);
        }
    }

    private void OnPageJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AlbumCollectionViewModel vm)
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

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
    }

    private void OnJumpToCategoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var targetKey = (sender as Control)?.Tag as string;
        if (string.IsNullOrWhiteSpace(targetKey))
            return;

        var scrollViewer = this.FindControl<ScrollViewer>("AlbumScrollViewer");
        if (scrollViewer == null)
            return;

        var target = targetKey switch
        {
            "pack" => this.FindControl<Control>("PackSectionAnchor"),
            "personal" => this.FindControl<Control>("PersonalRepositorySectionAnchor"),
            "community" => this.FindControl<Control>("CommunitySectionAnchor"),
            _ => null
        };

        if (target == null)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            scrollViewer.Offset = new Avalonia.Vector(
                scrollViewer.Offset.X,
                Math.Max(0, target.Bounds.Y));
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async Task LoadVisibleCategoryCoversAsync(ScrollViewer scrollViewer, AlbumCollectionViewModel vm)
    {
        var designerItems = new List<DesignerCategoryItemViewModel>();
        var communityItems = new List<CommunityCategoryItemViewModel>();

        foreach (var visual in this.GetSelfAndVisualDescendants())
        {
            if (visual is not Control control)
                continue;

            if (control.DataContext is not (DesignerCategoryItemViewModel or CommunityCategoryItemViewModel))
                continue;

            var top = control.Bounds.Top;
            var bottom = control.Bounds.Bottom;
            var viewportTop = scrollViewer.Offset.Y - 260;
            var viewportBottom = scrollViewer.Offset.Y + scrollViewer.Viewport.Height + 260;
            var isVisibleBand = bottom >= viewportTop && top <= viewportBottom;

            if (!isVisibleBand)
                continue;

            switch (control.DataContext)
            {
                case DesignerCategoryItemViewModel designer when !designerItems.Contains(designer):
                    designerItems.Add(designer);
                    break;
                case CommunityCategoryItemViewModel community when !communityItems.Contains(community):
                    communityItems.Add(community);
                    break;
            }
        }

        if (designerItems.Count > 0)
            await vm.EnsureCategoryCoversLoadedAsync(designerItems);
        if (communityItems.Count > 0)
            await vm.EnsureCommunityCoversLoadedAsync(communityItems);
    }
}
