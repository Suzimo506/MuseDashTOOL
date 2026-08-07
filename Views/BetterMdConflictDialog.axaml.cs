using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MdModManager.Services;

namespace MdModManager.Views;

public partial class BetterMdConflictDialog : Window
{
    public bool Confirmed { get; private set; }

    public BetterMdConflictDialog()
    {
        InitializeComponent();
    }

    private BetterMdConflictDialog(IEnumerable<BetterMdConflict> conflicts) : this()
    {
        ConflictListText.Text = string.Join("\n", conflicts.Select(conflict => conflict.DisplayName));
    }

    public static async Task<bool> ShowDialogAsync(Window owner, IReadOnlyList<BetterMdConflict> conflicts)
    {
        var dialog = new BetterMdConflictDialog(conflicts);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
