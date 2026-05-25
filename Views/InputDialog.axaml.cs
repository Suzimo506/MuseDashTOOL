using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace MdModManager.Views;

public partial class InputDialog : Window
{
    private string? _result;
    private bool _confirmed;

    public InputDialog()
    {
        InitializeComponent();
    }

    public static async Task<string?> ShowDialogAsync(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dialog = new InputDialog
        {
            Title = title
        };
        dialog.FindControl<TextBlock>("PromptText")!.Text = prompt;
        if (!string.IsNullOrEmpty(defaultValue))
        {
            var textBox = dialog.FindControl<TextBox>("InputTextBox");
            if (textBox != null)
            {
                textBox.Text = defaultValue;
            }
        }

        await dialog.ShowDialog(owner);
        return dialog._confirmed ? dialog._result : null;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var textBox = this.FindControl<TextBox>("InputTextBox");
        _result = textBox?.Text?.Trim();
        _confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = false;
        _result = null;
        Close();
    }
}
