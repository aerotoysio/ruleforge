using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using RuleForge.Core.Loader;
using RuleForge.Studio.Core.Connections;

namespace RuleForge.Studio.Views;

/// <summary>
/// Pick values out of a reference-data table: choose a datasource, which column to SHOW
/// (e.g. country name) and which column supplies the VALUE (e.g. ISO code), tick rows,
/// and the value-column entries are returned (to become filter chips).
/// </summary>
public partial class ReferencePickerDialog : Window
{
    public sealed partial class RowItem : ObservableObject
    {
        [ObservableProperty] private bool _isChecked;
        public string Display { get; init; } = "";
        public string Value { get; init; } = "";
        public string ValueHint => Display == Value ? "" : $"  →  {Value}";
    }

    private readonly Func<string, ReferenceSet?> _load;
    private ReferenceSet? _current;
    private readonly ObservableCollection<RowItem> _rows = new();

    public IReadOnlyList<string> SelectedValues { get; private set; } = [];

    public ReferencePickerDialog(IReadOnlyList<ReferenceSetSummary> sets, Func<string, ReferenceSet?> load)
    {
        InitializeComponent();
        _load = load;
        RowsList.ItemsSource = _rows;
        SetCombo.ItemsSource = sets;
        if (sets.Count > 0) SetCombo.SelectedIndex = 0;
    }

    private void OnSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SetCombo.SelectedItem is not ReferenceSetSummary summary) return;
        _current = _load(summary.Id);
        if (_current is null) return;

        DisplayCombo.ItemsSource = _current.Columns;
        ValueCombo.ItemsSource = _current.Columns;
        DisplayCombo.SelectedIndex = 0;
        ValueCombo.SelectedIndex = _current.Columns.Count > 1 ? 1 : 0;
        RebuildRows();
    }

    private void OnColumnsChanged(object sender, SelectionChangedEventArgs e) => RebuildRows();

    private void RebuildRows()
    {
        _rows.Clear();
        if (_current is null) return;
        var displayCol = DisplayCombo.SelectedItem as string ?? _current.Columns.FirstOrDefault() ?? "";
        var valueCol = ValueCombo.SelectedItem as string ?? displayCol;

        foreach (var row in _current.Rows)
        {
            _rows.Add(new RowItem
            {
                Display = Cell(row, displayCol),
                Value = Cell(row, valueCol),
            });
        }
    }

    private static string Cell(IReadOnlyDictionary<string, JsonElement> row, string col)
        => row.TryGetValue(col, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()
            : "";

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedValues = _rows.Where(r => r.IsChecked).Select(r => r.Value)
            .Where(v => v.Length > 0).Distinct().ToList();
        DialogResult = true;
    }
}
