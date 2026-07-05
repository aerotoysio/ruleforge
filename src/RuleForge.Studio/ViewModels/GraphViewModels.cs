using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Core.Models;

namespace RuleForge.Studio.ViewModels;

public enum ConnectorKind { Input, Pass, Fail, Default }

/// <summary>A connection point on a node. Nodify writes <see cref="Anchor"/> as the node moves.</summary>
public sealed partial class ConnectorViewModel : ObservableObject
{
    [ObservableProperty] private Point _anchor;
    [ObservableProperty] private bool _isConnected;

    public required string Title { get; init; }
    public required ConnectorKind Kind { get; init; }
    public required NodeViewModel Node { get; init; }

    public bool IsInput => Kind == ConnectorKind.Input;

    /// <summary>The edge branch this output represents (null for inputs).</summary>
    public EdgeBranch? Branch => Kind switch
    {
        ConnectorKind.Pass => EdgeBranch.Pass,
        ConnectorKind.Fail => EdgeBranch.Fail,
        ConnectorKind.Default => EdgeBranch.Default,
        _ => null,
    };
}

/// <summary>A node on the canvas, projected from a <see cref="RuleNode"/>.</summary>
public sealed partial class NodeViewModel : ObservableObject
{
    [ObservableProperty] private Point _location;
    [ObservableProperty] private bool _isSelected;

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string CategoryLabel { get; init; }
    public required NodeCategory Category { get; init; }
    public required Brush AccentBrush { get; init; }

    public ObservableCollection<ConnectorViewModel> Inputs { get; } = new();
    public ObservableCollection<ConnectorViewModel> Outputs { get; } = new();
}

/// <summary>An edge on the canvas between an output connector and an input connector.</summary>
public sealed class ConnectionViewModel
{
    public required ConnectorViewModel Source { get; init; }
    public required ConnectorViewModel Target { get; init; }
    public required Brush Stroke { get; init; }
}

/// <summary>Drives the rubber-band connection the user drags between connectors.</summary>
public sealed partial class PendingConnectionViewModel : ObservableObject
{
    private readonly GraphViewModel _graph;
    [ObservableProperty] private ConnectorViewModel? _source;

    public PendingConnectionViewModel(GraphViewModel graph) => _graph = graph;

    [RelayCommand]
    private void Start(ConnectorViewModel source) => Source = source;

    [RelayCommand]
    private void Finish(ConnectorViewModel? target)
    {
        if (Source is not null && target is not null)
            _graph.RequestConnect(Source, target);
        Source = null;
    }
}

/// <summary>
/// The editable canvas model. Built from a <see cref="Rule"/>; structural edits (connect /
/// disconnect) are raised as events so the owning MainViewModel folds them back into the rule
/// (the single source of truth) and re-evaluates.
/// </summary>
public sealed class GraphViewModel
{
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();
    public PendingConnectionViewModel PendingConnection { get; }

    /// <summary>Raised when the user draws an edge (output connector → input connector).</summary>
    public event Action<ConnectorViewModel, ConnectorViewModel>? ConnectRequested;
    /// <summary>Raised when the user disconnects an edge.</summary>
    public event Action<ConnectionViewModel>? DisconnectRequested;

    public GraphViewModel() => PendingConnection = new PendingConnectionViewModel(this);

    public void RequestConnect(ConnectorViewModel a, ConnectorViewModel b)
    {
        var output = a.IsInput ? (b.IsInput ? null : b) : a;
        var input = a.IsInput ? a : (b.IsInput ? b : null);
        if (output is null || input is null || ReferenceEquals(output.Node, input.Node)) return;
        ConnectRequested?.Invoke(output, input);
    }

    public void RequestDisconnect(ConnectionViewModel connection) => DisconnectRequested?.Invoke(connection);

    public static Brush BranchBrush(EdgeBranch? b)
    {
        var hex = b switch
        {
            EdgeBranch.Pass => "#16A34A",
            EdgeBranch.Fail => "#DC2626",
            _ => "#94A3B8",
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Project an engine <see cref="Rule"/> into the connector-based canvas model.</summary>
    public static GraphViewModel FromRule(Rule rule)
    {
        var graph = new GraphViewModel();

        var byId = new Dictionary<string, NodeViewModel>();
        foreach (var n in rule.Nodes)
        {
            var vm = new NodeViewModel
            {
                Id = n.Id,
                Title = string.IsNullOrWhiteSpace(n.Data.Label) ? n.Id : n.Data.Label,
                CategoryLabel = CategoryLabel(n.Data.Category),
                Category = n.Data.Category,
                AccentBrush = AccentFor(n.Data.Category),
                Location = new Point(n.Position.X, n.Position.Y),
            };
            AddConnectors(vm);
            graph.Nodes.Add(vm);
            byId[n.Id] = vm;
        }

        foreach (var e in rule.Edges)
        {
            if (!byId.TryGetValue(e.Source, out var s) || !byId.TryGetValue(e.Target, out var t))
                continue;
            var output = OutputFor(s, e.Branch);
            var input = t.Inputs.FirstOrDefault();
            if (output is null || input is null) continue;

            output.IsConnected = true;
            input.IsConnected = true;
            graph.Connections.Add(new ConnectionViewModel { Source = output, Target = input, Stroke = BranchBrush(e.Branch) });
        }

        return graph;
    }

    private static ConnectorViewModel? OutputFor(NodeViewModel node, EdgeBranch? branch)
        => node.Outputs.FirstOrDefault(o => o.Branch == branch) ?? node.Outputs.FirstOrDefault();

    private static void AddConnectors(NodeViewModel vm)
    {
        if (vm.Category != NodeCategory.Input)
            vm.Inputs.Add(new ConnectorViewModel { Title = "in", Kind = ConnectorKind.Input, Node = vm });

        switch (vm.Category)
        {
            case NodeCategory.Output:
                break; // sink — no outputs
            case NodeCategory.Filter:
            case NodeCategory.Logic:
            case NodeCategory.Assert:
                vm.Outputs.Add(new ConnectorViewModel { Title = "pass", Kind = ConnectorKind.Pass, Node = vm });
                vm.Outputs.Add(new ConnectorViewModel { Title = "fail", Kind = ConnectorKind.Fail, Node = vm });
                break;
            default:
                vm.Outputs.Add(new ConnectorViewModel { Title = "out", Kind = ConnectorKind.Default, Node = vm });
                break;
        }
    }

    private static string CategoryLabel(NodeCategory c) => c switch
    {
        NodeCategory.RuleRef => "sub-rule",
        NodeCategory.GroupBy => "group by",
        NodeCategory.FilterList => "filter list",
        NodeCategory.TextParse => "parse",
        _ => c.ToString().ToLowerInvariant(),
    };

    private static Brush AccentFor(NodeCategory c)
    {
        var hex = c switch
        {
            NodeCategory.Input or NodeCategory.Output => "#64748B",
            NodeCategory.Filter or NodeCategory.FilterList => "#2563EB",
            NodeCategory.Logic => "#7C3AED",
            NodeCategory.Product or NodeCategory.Constant => "#059669",
            NodeCategory.Mutator => "#D97706",
            NodeCategory.Calc => "#0891B2",
            NodeCategory.Reference => "#4F46E5",
            NodeCategory.RuleRef => "#DB2777",
            NodeCategory.Switch or NodeCategory.Assert or NodeCategory.Bucket => "#9333EA",
            NodeCategory.Iterator or NodeCategory.Merge => "#0D9488",
            _ => "#64748B",
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
