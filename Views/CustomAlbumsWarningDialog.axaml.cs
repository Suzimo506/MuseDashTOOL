using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.Services;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class CustomAlbumsWarningDialog : Window
{
    private readonly IConfigService? _configService;

    public CustomAlbumsWarningDialog()
    {
        InitializeComponent();
    }

    public CustomAlbumsWarningDialog(IConfigService configService) : this()
    {
        _configService = configService;
    }

    // Static helper method to show the dialog
    public static async Task ShowDialogAsync(Window owner, IConfigService configService)
    {
        // Check if suppressed
        if (configService.Config.SuppressCustomAlbumsWarning)
        {
            return;
        }

        var dialog = new CustomAlbumsWarningDialog(configService);
        dialog.ShowInTaskbar = true;
        var tcs = new TaskCompletionSource<bool>();
        dialog.Closed += (s, e) => tcs.TrySetResult(true);

        bool originalHitTest = true;
        Control? contentControl = owner?.Content as Control;
        if (contentControl != null)
        {
            originalHitTest = contentControl.IsHitTestVisible;
            contentControl.IsHitTestVisible = false;
        }

        if (owner != null)
        {
            dialog.Show(owner);
        }
        else
        {
            dialog.Show();
        }
        
        await tcs.Task;

        if (owner != null)
        {
            if (contentControl != null)
            {
                contentControl.IsHitTestVisible = originalHitTest;
            }
            if (owner.WindowState != WindowState.Minimized)
            {
                owner.Activate();
            }
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DontShowAgainCheckBox.IsChecked == true && _configService != null)
        {
            _configService.Config.SuppressCustomAlbumsWarning = true;
            _ = _configService.SaveAsync();
        }

        Close();
    }
}
