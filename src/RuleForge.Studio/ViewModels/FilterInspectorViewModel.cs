using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Studio.Core.Authoring;

namespace RuleForge.Studio.ViewModels;

/// <summary>A JSON-name + friendly-display pair for dropdowns.</summary>
public sealed record OptionItem(string Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The schema-aware string-filter editor: pick a string element from the request, choose an
/// operator, supply one value or a list of values, and — when the element sits inside a list —
/// choose how the list is judged (any/all/none/first/only) and what happens when it's missing.
/// </summary>
public sealed partial class FilterInspectorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<OptionItem> ArrayModeOptions = new[]
    {
        new OptionItem("any", "any item matches"),
        new OptionItem("all", "all items match"),
        new OptionItem("none", "no item matches"),
        new OptionItem("first", "the first item matches"),
        new OptionItem("only", "there is exactly one item and it matches"),
    };

    public static readonly IReadOnlyList<OptionItem> MissingModeOptions = new[]
    {
        new OptionItem("fail", "fail the filter"),
        new OptionItem("pass", "pass the filter"),
        new OptionItem("skip", "skip this path"),
    };

    public string NodeId { get; init; } = "";
    public IReadOnlyList<SchemaField> Fields { get; init; } = [];
    public IReadOnlyList<string> Operators { get; } = FilterEditing.StringOperators;
    public IReadOnlyList<OptionItem> ArrayModes => ArrayModeOptions;
    public IReadOnlyList<OptionItem> MissingModes => MissingModeOptions;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private SchemaField? _selectedField;
    [ObservableProperty] private string _operator = "equals";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private OptionItem _arrayMode = ArrayModeOptions[0];
    [ObservableProperty] private OptionItem _missingMode = MissingModeOptions[0];
    [ObservableProperty] private bool _caseInsensitive = true;
    [ObservableProperty] private bool _trim = true;

    /// <summary>Values for in / not_in — the request element should (or should not) be one of these.</summary>
    public ObservableCollection<string> Values { get; } = new();

    public bool IsListOperator => FilterEditing.IsListOperator(Operator);
    public bool ShowSingleValue => !FilterEditing.IsListOperator(Operator) && !FilterEditing.IsUnaryOperator(Operator);

    /// <summary>True when the chosen element sits inside a list (path contains [*]).</summary>
    public bool IsArrayPath => SelectedField?.Path.Contains("[*]") == true;

    /// <summary>The RHS handed to the config builder: single value or the values list.</summary>
    public IReadOnlyList<string> EffectiveValues =>
        IsListOperator ? Values.ToList()
        : ShowSingleValue && Value.Trim().Length > 0 ? [Value.Trim()]
        : [];

    partial void OnOperatorChanged(string value)
    {
        OnPropertyChanged(nameof(IsListOperator));
        OnPropertyChanged(nameof(ShowSingleValue));
    }

    partial void OnSelectedFieldChanged(SchemaField? value)
        => OnPropertyChanged(nameof(IsArrayPath));

    [RelayCommand]
    private void AddValue()
    {
        var v = NewValue.Trim();
        if (v.Length == 0 || Values.Contains(v)) return;
        Values.Add(v);
        NewValue = "";
    }

    [RelayCommand]
    private void RemoveValue(string value) => Values.Remove(value);
}
