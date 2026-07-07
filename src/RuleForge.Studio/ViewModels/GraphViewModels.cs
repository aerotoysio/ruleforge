using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Core.Models;
using RuleForge.Studio.Core.Authoring;

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

    /// <summary>What the node does, in plain English — user text or an auto-generated summary.</summary>
    public string Description { get; init; } = "";
    public bool HasDescription => Description.Length > 0;

    /// <summary>Dark same-hue text used on the pastel header band.</summary>
    public Brush HeaderTextBrush { get; init; } = Brushes.Black;

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
            var (headerBg, headerFg) = PaletteFor(n.Data.Category);
            var vm = new NodeViewModel
            {
                Id = n.Id,
                Title = string.IsNullOrWhiteSpace(n.Data.Label) ? n.Id : n.Data.Label,
                CategoryLabel = CategoryLabel(n.Data.Category),
                Category = n.Data.Category,
                AccentBrush = headerBg,
                HeaderTextBrush = headerFg,
                Description = DescribeNode(n),
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

    /// <summary>User description when present; otherwise a plain-English summary for filters.</summary>
    private static string DescribeNode(RuleNode n)
    {
        if (!string.IsNullOrWhiteSpace(n.Data.Description))
            return n.Data.Description.Trim();

        if (n.Data.Category == NodeCategory.Filter
            && FilterEditing.ReadStringFilter(n.Data.Config) is { } cfg)
            return FilterEditing.Summarize(cfg);

        return "";
    }

    private static string CategoryLabel(NodeCategory c) => c switch
    {
        NodeCategory.RuleRef => "sub-rule",
        NodeCategory.GroupBy => "group by",
        NodeCategory.FilterList => "filter list",
        NodeCategory.TextParse => "parse",
        _ => c.ToString().ToLowerInvariant(),
    };

    /// <summary>Pastel header background + dark same-hue text, per category.</summary>
    public static (Brush Background, Brush Text) PaletteFor(NodeCategory c)
    {
        var (bg, fg) = c switch
        {
            NodeCategory.Input or NodeCategory.Output => ("#E5E9F2", "#46536B"),
            NodeCategory.Filter or NodeCategory.FilterList => ("#DCEAFB", "#31599B"),
            NodeCategory.Logic => ("#E9E3FA", "#5C48A8"),
            NodeCategory.Product or NodeCategory.Constant => ("#D9F2E5", "#20714E"),
            NodeCategory.Mutator => ("#FBEDD3", "#8F5F1D"),
            NodeCategory.Calc => ("#D8F1F7", "#1D6A80"),
            NodeCategory.Reference => ("#E2E6FA", "#4550A8"),
            NodeCategory.RuleRef => ("#FADFE9", "#A0446C"),
            NodeCategory.Switch or NodeCategory.Assert or NodeCategory.Bucket => ("#EFE2F9", "#71459B"),
            NodeCategory.Iterator or NodeCategory.Merge => ("#D6F0EC", "#1E6E62"),
            _ => ("#E5E9F2", "#46536B"),
        };
        return (Freeze(bg), Freeze(fg));
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
