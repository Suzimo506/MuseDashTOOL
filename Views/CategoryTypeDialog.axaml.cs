using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace MdModManager.Views;

public enum CategoryCreateScope
{
    Normal,
    Candidate
}

public partial class CategoryTypeDialog : Window
{
    private CategoryCreateScope? _result;

    public CategoryTypeDialog()
    {
        InitializeComponent();
    }

    public static async Task<CategoryCreateScope?> ShowDialogAsync(Window owner)
    {
        var dialog = new CategoryTypeDialog();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void OnNormalClick(object? sender, RoutedEventArgs e)
    {
        _result = CategoryCreateScope.Normal;
        Close();
    }

    private void OnCandidateClick(object? sender, RoutedEventArgs e)
    {
        _result = CategoryCreateScope.Candidate;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }
}
