using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RuleForge.Studio.ViewModels;

namespace RuleForge.Studio.Views;

public partial class FilterEditorDialog : Window
{
    public FilterEditorDialog()
    {
        InitializeComponent();
    }

    private FilterInspectorViewModel Vm => (FilterInspectorViewModel)DataContext;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Enter in a new-value box adds the value instead of closing the dialog.</summary>
    private void OnNewValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((sender as FrameworkElement)?.DataContext as ConditionViewModel)?.AddValueCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Open the reference-data picker and add the chosen values to this condition's chips.</summary>
    private void OnPickFromReference(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConditionViewModel condition) return;
        if (Vm.ListReferenceSets is null || Vm.LoadReferenceSet is null) return;

        var sets = Vm.ListReferenceSets();
        if (sets.Count == 0)
        {
            MessageBox.Show(this, "No reference data available on this connection yet.",
                "RuleForge Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ReferencePickerDialog(sets, Vm.LoadReferenceSet) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            foreach (var v in dlg.SelectedValues)
                if (!condition.Values.Contains(v))
                    condition.Values.Add(v);
        }
    }
}
