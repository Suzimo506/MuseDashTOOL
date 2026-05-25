       using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MdModManager.Views.Controls;

public class MarqueeText : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarqueeText, string>(nameof(Text));

    public static new readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<MarqueeText>();

    public static new readonly StyledProperty<FontWeight> FontWeightProperty =
        TextBlock.FontWeightProperty.AddOwner<MarqueeText>();

    public static new readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<MarqueeText>();

    public static readonly StyledProperty<bool> IsScrollingEnabledProperty =
        AvaloniaProperty.Register<MarqueeText, bool>(nameof(IsScrollingEnabled), defaultValue: true);

    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<MarqueeText, string>(nameof(SearchText));

    public static readonly StyledProperty<string> HighlightTextProperty =
        AvaloniaProperty.Register<MarqueeText, string>(nameof(HighlightText));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public new double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public new FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public new IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsScrollingEnabled
    {
        get => GetValue(IsScrollingEnabledProperty);
        set => SetValue(IsScrollingEnabledProperty, value);
    }

    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string HighlightText
    {
        get => GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    private readonly Canvas _canvas;
    private readonly TextBlock _textBlock1;
    private readonly TextBlock _textBlock2;
    private DispatcherTimer? _timer;
    private double _offset;
    private double _textWidth;
    private const double Speed = 0.5; // 每次时钟滴答移动的像素数
    private const double Spacing = 30.0; // 重复文本之间的间距

    public MarqueeText()
    {
        _textBlock1 = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        _textBlock2 = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            IsVisible = false
        };

        _canvas = new Canvas { ClipToBounds = true };
        _canvas.Children.Add(_textBlock1);
        _canvas.Children.Add(_textBlock2);

        Content = _canvas;

        this.GetObservable(TextProperty).Subscribe(_ => UpdateText());
        this.GetObservable(FontSizeProperty).Subscribe(_ => UpdateStyle());
        this.GetObservable(FontWeightProperty).Subscribe(_ => UpdateStyle());
        this.GetObservable(ForegroundProperty).Subscribe(_ => UpdateStyle());
        this.GetObservable(IsScrollingEnabledProperty).Subscribe(_ => CheckScrolling());
        this.GetObservable(BoundsProperty).Subscribe(_ => CheckScrolling());
        this.GetObservable(SearchTextProperty).Subscribe(_ => UpdateHighlight());
        this.GetObservable(HighlightTextProperty).Subscribe(_ => UpdateHighlight());
    }

    private void UpdateHighlight()
    {
        // Forward to the helper on internal text blocks
        MdModManager.Helpers.TextBlockHelper.SetSearchText(_textBlock1, SearchText);
        MdModManager.Helpers.TextBlockHelper.SetHighlightText(_textBlock1, HighlightText);
        MdModManager.Helpers.TextBlockHelper.SetSearchText(_textBlock2, SearchText);
        MdModManager.Helpers.TextBlockHelper.SetHighlightText(_textBlock2, HighlightText);
    }

    private void UpdateStyle()
    {
        _textBlock1.FontSize = FontSize;
        _textBlock2.FontSize = FontSize;
        _textBlock1.FontWeight = FontWeight;
        _textBlock2.FontWeight = FontWeight;
        _textBlock1.Foreground = Foreground;
        _textBlock2.Foreground = Foreground;
        CheckScrolling();
    }

    private void UpdateText()
    {
        _textBlock1.Text = Text;
        _textBlock2.Text = Text;
        UpdateHighlight();
        CheckScrolling();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _textBlock1.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _canvas.Measure(availableSize);
        return _textBlock1.DesiredSize;
    }

    private void CheckScrolling()
    {
        if (string.IsNullOrEmpty(Text))
        {
            StopScrolling();
            return;
        }

        // Measure text width
        _textBlock1.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = _textBlock1.DesiredSize.Width;

        // 滚动关闭时根据宽度自适应居中或截断
        if (!IsScrollingEnabled)
        {
            StopScrolling();
            _textBlock1.TextTrimming = TextTrimming.CharacterEllipsis;
            if (_textWidth > Bounds.Width && Bounds.Width > 0)
            {
                Canvas.SetLeft(_textBlock1, 0);
            }
            else
            {
                double left = Bounds.Width > 0 ? (Bounds.Width - _textWidth) / 2 : 0;
                Canvas.SetLeft(_textBlock1, left);
            }
            return;
        }

        _textBlock1.TextTrimming = TextTrimming.None;

        // If text is wider than control, start scrolling
        if (_textWidth > Bounds.Width && Bounds.Width > 0)
        {
            StartScrolling();
        }
        else
        {
            StopScrolling();
            double left = Bounds.Width > 0 ? (Bounds.Width - _textWidth) / 2 : 0;
            Canvas.SetLeft(_textBlock1, left);
        }
    }

    private void StartScrolling()
    {
        if (_timer != null) return;
        
        _textBlock2.IsVisible = true;
        _offset = 0;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20) // ~50fps
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopScrolling()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        _textBlock2.IsVisible = false;
        _offset = 0;
        Canvas.SetLeft(_textBlock1, 0);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (Bounds.Width <= 0 || _textWidth <= 0) return;

        _offset -= Speed;

        // When the first text block has fully scrolled out plus spacing
        if (_offset <= -(_textWidth + Spacing))
        {
            _offset += (_textWidth + Spacing);
        }

        Canvas.SetLeft(_textBlock1, _offset);
        Canvas.SetLeft(_textBlock2, _offset + _textWidth + Spacing);
    }

    protected override void OnDetachedFromLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        StopScrolling();
        base.OnDetachedFromLogicalTree(e);
    }
}
