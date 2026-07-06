using System.Windows;

namespace RuleForge.Studio.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public string Value => ValueBox.Text.Trim();

    /// <summary>Show a prompt; returns the entered text, or null if cancelled/empty.</summary>
    public static string? Ask(Window owner, string prompt, string title, string initial = "")
    {
        var dlg = new InputDialog { Owner = owner, Title = title };
        dlg.PromptText.Text = prompt;
        dlg.ValueBox.Text = initial;
        dlg.ValueBox.Focus();
        dlg.ValueBox.SelectAll();
        return dlg.ShowDialog() == true && dlg.Value.Length > 0 ? dlg.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
