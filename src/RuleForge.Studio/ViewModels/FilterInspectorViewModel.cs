using CommunityToolkit.Mvvm.ComponentModel;
using RuleForge.Studio.Core.Authoring;

namespace RuleForge.Studio.ViewModels;

/// <summary>
/// The schema-aware filter editor shown when a filter node is selected: pick a request field
/// (from the rule's inputSchema), pick an operator, enter a value. Kept UI-only; the owning
/// MainViewModel turns it into engine config on Apply.
/// </summary>
public sealed partial class FilterInspectorViewModel : ObservableObject
{
    public string NodeId { get; init; } = "";
    public string NodeLabel { get; init; } = "";
    public IReadOnlyList<SchemaField> Fields { get; init; } = [];
    public IReadOnlyList<string> Operators { get; } = FilterEditing.StringOperators;

    [ObservableProperty] private SchemaField? _selectedField;
    [ObservableProperty] private string _operator = "equals";
    [ObservableProperty] private string _value = "";

    /// <summary>True when the operator needs no value (is_null / is_empty).</summary>
    public bool ValueEnabled => Operator is not ("is_null" or "is_empty");

    partial void OnOperatorChanged(string value) => OnPropertyChanged(nameof(ValueEnabled));
}
