using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace MdModManager.Views;

public enum DeduplicationScanScope
{
    CustomAlbums,
    Library
}

public partial class DeduplicationScopeDialog : Window
{
    private DeduplicationScanScope? _result;

    public DeduplicationScopeDialog()
    {
        InitializeComponent();
    }

    public static async Task<DeduplicationScanScope?> ShowDialogAsync(Window owner)
    {
        var dialog = new DeduplicationScopeDialog();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void OnCustomAlbumsClick(object? sender, RoutedEventArgs e)
    {
        _result = DeduplicationScanScope.CustomAlbums;
        Close();
    }

    private void OnLibraryClick(object? sender, RoutedEventArgs e)
    {
        _result = DeduplicationScanScope.Library;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }
}
