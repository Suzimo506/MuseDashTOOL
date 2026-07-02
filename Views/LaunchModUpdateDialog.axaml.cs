using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.Services;

namespace MdModManager.Views;

public enum LaunchModUpdateDialogResult
{
    Cancel,
    Continue,
    UpdateThenContinue
}

public partial class LaunchModUpdateDialog : Window
{
    public LaunchModUpdateDialog()
    {
        InitializeComponent();
    }

    private LaunchModUpdateDialog(IReadOnlyList<ModUpdateCandidate> updates) : this()
    {
        UpdateListText.Text = BuildUpdateListText(updates);
    }

    public LaunchModUpdateDialogResult Result { get; private set; } = LaunchModUpdateDialogResult.Cancel;
    public bool DontShowAgain => DontShowAgainCheckBox.IsChecked == true;

    public static async Task<(LaunchModUpdateDialogResult Result, bool DontShowAgain)> ShowDialogAsync(
        Window owner,
        IReadOnlyList<ModUpdateCandidate> updates)
    {
        var dialog = new LaunchModUpdateDialog(updates);
        dialog.ShowInTaskbar = true;

        var contentControl = owner.Content as Control;
        var originalHitTest = contentControl?.IsHitTestVisible ?? true;
        if (contentControl != null)
        {
            contentControl.IsHitTestVisible = false;
        }

        await dialog.ShowDialog(owner);

        if (contentControl != null)
        {
            contentControl.IsHitTestVisible = originalHitTest;
        }

        if (owner.WindowState != WindowState.Minimized)
        {
            owner.Activate();
        }

        return (dialog.Result, dialog.DontShowAgain);
    }

    private static string BuildUpdateListText(IReadOnlyList<ModUpdateCandidate> updates)
    {
        var builder = new StringBuilder();
        foreach (var update in updates.Take(12))
        {
            builder.Append("• ");
            builder.Append(update.Name);
            if (!string.IsNullOrWhiteSpace(update.LocalVersion) ||
                !string.IsNullOrWhiteSpace(update.RemoteVersion))
            {
                builder.Append("  ");
                builder.Append(update.LocalVersion);
                builder.Append(" -> ");
                builder.Append(update.RemoteVersion);
            }
            builder.AppendLine();
        }

        if (updates.Count > 12)
        {
            builder.AppendLine($"以及另外 {updates.Count - 12} 个模组...");
        }

        return builder.ToString().TrimEnd();
    }

    private void OnContinueClick(object? sender, RoutedEventArgs e)
    {
        Result = LaunchModUpdateDialogResult.Continue;
        Close();
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        Result = LaunchModUpdateDialogResult.UpdateThenContinue;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = LaunchModUpdateDialogResult.Cancel;
        Close();
    }
}
