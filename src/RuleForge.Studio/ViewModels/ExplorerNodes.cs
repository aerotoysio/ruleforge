using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RuleForge.Studio.Core.Connections;

namespace RuleForge.Studio.ViewModels;

/// <summary>Base for Object Explorer tree nodes.</summary>
public abstract partial class ExplorerNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;

    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public bool IsFolder { get; init; }
    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = new();
}

/// <summary>Root node for a connection (local workspace / DocumentForge).</summary>
public sealed class ConnectionNodeViewModel : ExplorerNodeViewModel
{
    public ConnectionNodeViewModel() { IsFolder = true; Glyph = "◈"; }
}

/// <summary>A grouping folder (Rules, Reference sets, Products & templates, …).</summary>
public sealed class FolderNodeViewModel : ExplorerNodeViewModel
{
    public FolderNodeViewModel() { IsFolder = true; Glyph = "▸"; }
}

/// <summary>A rule leaf — selecting it opens the rule in the designer + harness.</summary>
public sealed class RuleNodeViewModel : ExplorerNodeViewModel
{
    public required RuleSummary Rule { get; init; }
}

/// <summary>A reference-set (datasource) leaf — selecting it opens the datasource grid.</summary>
public sealed class ReferenceSetNodeViewModel : ExplorerNodeViewModel
{
    public required ReferenceSetSummary ReferenceSet { get; init; }
}

/// <summary>A non-selectable informational leaf (e.g. "coming soon").</summary>
public sealed class MessageNodeViewModel : ExplorerNodeViewModel
{
    public MessageNodeViewModel() { Glyph = "·"; }
}
