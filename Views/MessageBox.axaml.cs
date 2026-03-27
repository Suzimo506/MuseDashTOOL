using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class MessageBox : Window
{
    private bool _confirmed;
    private TaskCompletionSource<bool> _tcs = new();

    public MessageBox()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowDialogAsync(Window owner, string message, bool showCancel = false)
    {
        var dialog = new MessageBox();
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        
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
