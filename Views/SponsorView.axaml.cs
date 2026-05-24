using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using Avalonia.Input;

namespace MdModManager.Views;

// 赞助打赏页面视图
public partial class SponsorView : UserControl
{
    private DispatcherTimer? _scrollTimer;
    private ScrollViewer? _scrollViewer;
    private bool _isUserHovering;
    private double _scrollSpeed = 0.5;

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
    }
}
