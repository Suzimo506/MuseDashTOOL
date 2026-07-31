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
        => await ShowDialogAsync(owner, message, showCancel, null, null);

    public static async Task<bool> ShowDialogAsync(
        Window owner,
        string message,
        bool showCancel,
        string? confirmText,
        string? cancelText)
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
        dialog.SetButtonText(confirmText, cancelText);
        
        if (showCancel)
        {
            dialog.FindControl<Button>("CancelButton")!.IsVisible = true;
        }

        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    private void SetButtonText(string? confirmText, string? cancelText)
    {
        var confirmButton = this.FindControl<Button>("ConfirmButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;

        if (!string.IsNullOrWhiteSpace(confirmText))
            confirmButton.Content = confirmText;

        if (!string.IsNullOrWhiteSpace(cancelText))
            cancelButton.Content = cancelText;

        if (!string.IsNullOrWhiteSpace(confirmText) || !string.IsNullOrWhiteSpace(cancelText))
        {
            confirmButton.Width = double.NaN;
            cancelButton.Width = double.NaN;
            confirmButton.MinWidth = 100;
            cancelButton.MinWidth = 100;
            confirmButton.Padding = new Avalonia.Thickness(16, 0);
            cancelButton.Padding = new Avalonia.Thickness(16, 0);
            var buttonPanel = this.FindControl<StackPanel>("ButtonPanel")!;
            buttonPanel.Spacing = 12;

            if (!string.IsNullOrWhiteSpace(confirmText) && !string.IsNullOrWhiteSpace(cancelText))
            {
                buttonPanel.Children.Clear();
                buttonPanel.Children.Add(confirmButton);
                buttonPanel.Children.Add(cancelButton);
            }
        }
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
