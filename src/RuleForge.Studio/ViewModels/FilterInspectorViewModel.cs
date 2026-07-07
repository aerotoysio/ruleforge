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

/// <summary>A pickable request element in the field list (hierarchy via indentation).</summary>
public sealed class FieldItem
{
    public FieldItem(SchemaField field)
    {
        Field = field;
        var segments = field.Path.TrimStart('$', '.').Split('.');
        Depth = segments.Length - 1;
        LeafName = segments[^1];
    }

    public SchemaField Field { get; }
    public int Depth { get; }
    public string LeafName { get; }
    public string Path => Field.Path;
    public string TypeName => Field.Type.ToString().ToLowerInvariant();
    public double Indent => Depth * 16;

    public bool Matches(string search)
        => search.Length == 0 || Path.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One condition row: operator + a value or a set of values (chips).</summary>
public sealed partial class ConditionViewModel : ObservableObject
{
    [ObservableProperty] private string _operator = "equals";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _newValue = "";

    public IReadOnlyList<string> Operators { get; } = FilterEditing.StringOperators;
    public ObservableCollection<string> Values { get; } = new();

    public bool IsListOperator => FilterEditing.IsListOperator(Operator);
    public bool ShowSingleValue => !FilterEditing.IsListOperator(Operator) && !FilterEditing.IsUnaryOperator(Operator);

    partial void OnOperatorChanged(string value)
    {
        OnPropertyChanged(nameof(IsListOperator));
        OnPropertyChanged(nameof(ShowSingleValue));
    }

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

    public FilterEditing.FilterCondition ToCondition()
        => new(Operator,
            IsListOperator ? Values.ToList()
            : ShowSingleValue && Value.Trim().Length > 0 ? [Value.Trim()]
            : []);
}

/// <summary>
/// The string-filter editor: a searchable, type-filtered field hierarchy on the left; a stack of
/// conditions (combined ALL/ANY) on the right — "in [EU countries] AND not equals CH" is one node.
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

    public static readonly IReadOnlyList<OptionItem> MatchModeOptions = new[]
    {
        new OptionItem("all", "ALL conditions must match (AND)"),
        new OptionItem("any", "ANY condition may match (OR)"),
    };

    private readonly List<FieldItem> _allFields;

    public FilterInspectorViewModel(IReadOnlyList<SchemaField> fields)
    {
        _allFields = fields.Select(f => new FieldItem(f)).ToList();
        FilteredFields = new ObservableCollection<FieldItem>(_allFields);
        Conditions.Add(new ConditionViewModel());
        Conditions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleConditions));
    }

    public string NodeId { get; init; } = "";
    public IReadOnlyList<OptionItem> ArrayModes => ArrayModeOptions;
    public IReadOnlyList<OptionItem> MissingModes => MissingModeOptions;
    public IReadOnlyList<OptionItem> MatchModes => MatchModeOptions;

    /// <summary>Loads reference sets for the "pick from reference data" flow (set by the shell).</summary>
    public Func<IReadOnlyList<Core.Connections.ReferenceSetSummary>>? ListReferenceSets { get; init; }
    public Func<string, RuleForge.Core.Loader.ReferenceSet?>? LoadReferenceSet { get; init; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _fieldSearch = "";
    [ObservableProperty] private FieldItem? _selectedField;
    [ObservableProperty] private OptionItem _matchMode = MatchModeOptions[0];
    [ObservableProperty] private OptionItem _arrayMode = ArrayModeOptions[0];
    [ObservableProperty] private OptionItem _missingMode = MissingModeOptions[0];
    [ObservableProperty] private bool _caseInsensitive = true;
    [ObservableProperty] private bool _trim = true;

    public ObservableCollection<FieldItem> FilteredFields { get; }
    public ObservableCollection<ConditionViewModel> Conditions { get; } = new();

    public bool HasMultipleConditions => Conditions.Count > 1;
    public bool IsArrayPath => SelectedField?.Path.Contains("[*]") == true;

    partial void OnFieldSearchChanged(string value)
    {
        FilteredFields.Clear();
        foreach (var f in _allFields.Where(f => f.Matches(value.Trim())))
            FilteredFields.Add(f);
    }

    partial void OnSelectedFieldChanged(FieldItem? value)
        => OnPropertyChanged(nameof(IsArrayPath));

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new ConditionViewModel());

    [RelayCommand]
    private void RemoveCondition(ConditionViewModel condition)
    {
        if (Conditions.Count > 1) Conditions.Remove(condition);
    }

    /// <summary>Select the field item whose path matches (used when opening an existing config).</summary>
    public void SelectPath(string? path)
        => SelectedField = _allFields.FirstOrDefault(f => f.Path == path) ?? _allFields.FirstOrDefault();

    public IReadOnlyList<FilterEditing.FilterCondition> BuildConditions()
        => Conditions.Select(c => c.ToCondition())
            .Where(c => c.Values.Count > 0 || FilterEditing.IsUnaryOperator(c.Operator))
            .ToList();
}
