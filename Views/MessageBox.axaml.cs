using Avalonia.Controls;
using Avalonia.Interactivity;
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

    public static async Task<bool> ShowDialogAsync(Window owner, string message, bool showCancel = false)
    {
        var dialog = new MessageBox();
        if (message.Length > 70)
        {
            dialog.Width = 520;
            dialog.Height = 240;
        }
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        
        if (showCancel)
        {
            dialog.FindControl<Button>("CancelButton")!.IsVisible = true;
        }

        await dialog.ShowDialog(owner);
        return dialog._confirmed;
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
