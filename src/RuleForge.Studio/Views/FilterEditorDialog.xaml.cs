using System.Windows;

namespace RuleForge.Studio.Views;

public partial class FilterEditorDialog : Window
{
    public FilterEditorDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
