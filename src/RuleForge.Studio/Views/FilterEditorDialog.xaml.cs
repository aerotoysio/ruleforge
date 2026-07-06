using System.Windows;
using System.Windows.Input;
using RuleForge.Studio.ViewModels;

namespace RuleForge.Studio.Views;

public partial class FilterEditorDialog : Window
{
    public FilterEditorDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Enter in the new-value box adds the value instead of closing the dialog.</summary>
    private void OnNewValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (DataContext as FilterInspectorViewModel)?.AddValueCommand.Execute(null);
        e.Handled = true;
    }
}
