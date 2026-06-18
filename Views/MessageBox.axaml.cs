using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class MessageBox : Window
{
    private bool _confirmed;
    public MessageBox()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowDialogAsync(Window owner, string message, bool showCancel = false, string? footerMessage = null)
    {
        var dialog = new MessageBox();
        if (message.Length > 70)
        {
            dialog.Width = 720;
            dialog.Height = 320;
        }
        else
        {
            dialog.SizeToContent = SizeToContent.Height;
        }
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        dialog.SetFooterMessage(footerMessage);
        
        if (showCancel)
        {
            dialog.FindControl<Button>("CancelButton")!.IsVisible = true;
        }

        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    private void SetFooterMessage(string? footerMessage)
    {
        if (string.IsNullOrWhiteSpace(footerMessage))
            return;

        var footerText = this.FindControl<TextBlock>("MessageFooterText")!;
        footerText.Text = footerMessage;
        footerText.Foreground = new SolidColorBrush(Color.Parse("#4DA3FF"));
        footerText.IsVisible = true;
    }

    public static async Task<bool> ShowDialogWithImageAsync(Window owner, string message, string imageAssetUri, bool showCancel = false)
    {
        var dialog = new MessageBox
        {
            Width = 560,
            Height = 430
        };

        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        dialog.FindControl<Border>("MessageImageHost")!.IsVisible = true;

        using var stream = AssetLoader.Open(new Uri(imageAssetUri));
        dialog.FindControl<Image>("MessageImage")!.Source = new Bitmap(stream);

        if (showCancel)
        {
            dialog.FindControl<Button>("CancelButton")!.IsVisible = true;
        }

        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = false;
        Close();
    }
}
