using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;
using Avalonia.Input;
using MdModManager.ViewModels;

namespace MdModManager.Views;

// 赞助打赏页面视图
public partial class SponsorView : UserControl
{
    private DispatcherTimer? _scrollTimer;
    private ScrollViewer? _scrollViewer;
    private bool _isUserHovering;
    private double _scrollSpeed = 0.5;
    private SponsorViewModel? _viewModel;

    public SponsorView()
    {
        InitializeComponent();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        _scrollViewer = this.FindControl<ScrollViewer>("SponsorScrollViewer");
        if (_scrollViewer != null)
        {
            _scrollViewer.PointerEntered += OnPointerEntered;
            _scrollViewer.PointerExited += OnPointerExited;
            
            _scrollTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(30),
                DispatcherPriority.Background,
                OnScrollTimerTick);
            _scrollTimer.Start();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        
        _viewModel = DataContext as SponsorViewModel;
        
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SponsorViewModel.IsQRCodeVisible))
        {
            if (DataContext is SponsorViewModel vm && vm.IsQRCodeVisible)
            {
                var leftScrollViewer = this.FindControl<ScrollViewer>("LeftScrollViewer");
                if (leftScrollViewer != null)
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        // 等待展开动画开始后平滑滚动到底部
                        await Task.Delay(80);
                        await SmoothScrollToBottomAsync(leftScrollViewer);
                    }, DispatcherPriority.Normal);
                }
            }
        }
    }

    private async Task SmoothScrollToBottomAsync(ScrollViewer scrollViewer)
    {
        // 持续滚动35步，以配合展开动画的时长
        int steps = 35;
        int delayMs = 10;
        for (int i = 0; i < steps; i++)
        {
            await Task.Delay(delayMs);
            double maxScroll = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
            if (maxScroll <= 0) continue;
            
            double currentY = scrollViewer.Offset.Y;
            double targetY = maxScroll;
            double nextY = currentY + (targetY - currentY) * 0.25; // 渐进缓动
            
            if (Math.Abs(targetY - nextY) < 1.0)
            {
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetY);
                break;
            }
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextY);
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _isUserHovering = true;
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _isUserHovering = false;
    }

    private void OnScrollTimerTick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null || _isUserHovering) return;

        double maxScroll = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;
        if (maxScroll <= 0) return;

        double currentOffset = _scrollViewer.Offset.Y;
        double nextOffset = currentOffset + _scrollSpeed;

        if (nextOffset >= maxScroll)
        {
            _scrollViewer.Offset = new Vector(0, 0);
        }
        else
        {
            _scrollViewer.Offset = new Vector(0, nextOffset);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scrollTimer != null)
        {
            _scrollTimer.Stop();
            _scrollTimer = null;
        }
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }
}
