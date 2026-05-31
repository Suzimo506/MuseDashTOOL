using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class TutorialDialog : Window
{
    private bool _dontRemind;

    public TutorialDialog()
    {
        InitializeComponent();
    }

    // 静态显示对话框，返回用户是否点击了“不再提醒”
    public static async Task<bool> ShowDialogAsync(Window owner, string title, string message)
    {
        var dialog = new TutorialDialog();
        dialog.Title = title;

        // 获取显示文本的 TextBlock
        var textBlock = dialog.FindControl<TextBlock>("MessageText");
        if (textBlock != null)
        {
            textBlock.Text = message;
        }

        // 依据文字多少动态决定窗口大小，保证即使是短文本也有充足高度，绝不产生滚动条且与其它弹窗对齐
        if (message.Length > 200)
        {
            dialog.Height = 400;
        }
        else
        {
            dialog.Height = 340;
        }

        // 监听滚动与布局变化以动态更新向下箭头显示
        var scrollViewer = dialog.FindControl<ScrollViewer>("MessageScrollViewer");
        if (scrollViewer != null)
        {
            scrollViewer.PropertyChanged += (s, e) =>
            {
                if (e.Property == ScrollViewer.OffsetProperty ||
                    e.Property == ScrollViewer.ViewportProperty ||
                    e.Property == ScrollViewer.ExtentProperty)
                {
                    dialog.UpdateArrowVisibility();
                }
            };
        }

        await dialog.ShowDialog(owner);
        return dialog._dontRemind;
    }

    // 动态更新向下箭头可见性
    private void UpdateArrowVisibility()
    {
        var scrollViewer = this.FindControl<ScrollViewer>("MessageScrollViewer");
        var arrow = this.FindControl<Avalonia.Controls.Shapes.Path>("DownArrowIcon");
        if (scrollViewer != null && arrow != null)
        {
            var extentHeight = scrollViewer.Extent.Height;
            var viewportHeight = scrollViewer.Viewport.Height;
            var offsetY = scrollViewer.Offset.Y;

            // 如果内容高度大于可见高度，且未滚动到底部，则显示向下箭头
            bool hasMore = extentHeight > viewportHeight && offsetY < (extentHeight - viewportHeight - 5);
            arrow.IsVisible = hasMore;
        }
    }

    // 处理确定点击
    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var checkBox = this.FindControl<CheckBox>("DontShowAgainCheckBox");
        _dontRemind = checkBox?.IsChecked == true;
        Close();
    }
}
