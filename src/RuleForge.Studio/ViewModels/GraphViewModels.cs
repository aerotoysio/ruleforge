using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RuleForge.Core.Models;

namespace RuleForge.Studio.ViewModels;

/// <summary>A node on the Nodify canvas, projected from a <see cref="RuleNode"/>.</summary>
public sealed partial class NodeViewModel : ObservableObject
{
    [ObservableProperty] private Point _location;
    [ObservableProperty] private bool _isSelected;

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string CategoryLabel { get; init; } = "";
    public Brush AccentBrush { get; init; } = Brushes.SlateGray;

    /// <summary>Anchor points used to draw connections (approximated from node box geometry).</summary>
    public Point OutputAnchor => new(Location.X + NodeWidth, Location.Y + NodeHeight / 2);
    public Point InputAnchor => new(Location.X, Location.Y + NodeHeight / 2);

    public const double NodeWidth = 172;
    public const double NodeHeight = 68;

    // Keep connection anchors in sync when the node is dragged.
    partial void OnLocationChanged(Point value)
    {
        OnPropertyChanged(nameof(OutputAnchor));
        OnPropertyChanged(nameof(InputAnchor));
    }
}

/// <summary>
/// A directed edge on the canvas, projected from a <see cref="RuleEdge"/>. Its endpoints are
/// derived live from the connected nodes so dragging a node moves the wire with it.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    private readonly NodeViewModel _sourceNode;
    private readonly NodeViewModel _targetNode;

    public ConnectionViewModel(NodeViewModel source, NodeViewModel target)
    {
        _sourceNode = source;
        _targetNode = target;
        _sourceNode.PropertyChanged += OnEndpointChanged;
        _targetNode.PropertyChanged += OnEndpointChanged;
    }

    public string Id { get; init; } = "";
    public Brush Stroke { get; init; } = Brushes.Gray;
    public string? BranchLabel { get; init; }

    public Point Source => _sourceNode.OutputAnchor;
    public Point Target => _targetNode.InputAnchor;

    private void OnEndpointChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeViewModel.OutputAnchor)
            or nameof(NodeViewModel.InputAnchor)
            or nameof(NodeViewModel.Location))
        {
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(Target));
        }
    }
}

/// <summary>The whole graph the Nodify editor binds to.</summary>
public sealed class RuleGraphViewModel
{
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

    /// <summary>Project an engine <see cref="Rule"/> into canvas view-models (light palette).</summary>
    public static RuleGraphViewModel FromRule(Rule rule)
    {
        var graph = new RuleGraphViewModel();

        // Normalise positions so the graph starts near the top-left of the canvas.
        var minX = rule.Nodes.Count > 0 ? rule.Nodes.Min(n => n.Position.X) : 0;
        var minY = rule.Nodes.Count > 0 ? rule.Nodes.Min(n => n.Position.Y) : 0;
        const double pad = 48;

        var byId = new Dictionary<string, NodeViewModel>();
        foreach (var n in rule.Nodes)
        {
            var vm = new NodeViewModel
            {
                Id = n.Id,
                Title = string.IsNullOrWhiteSpace(n.Data.Label) ? n.Id : n.Data.Label,
                CategoryLabel = CategoryLabel(n.Data.Category),
                AccentBrush = AccentFor(n.Data.Category),
                Location = new Point(n.Position.X - minX + pad, n.Position.Y - minY + pad),
            };
            graph.Nodes.Add(vm);
            byId[n.Id] = vm;
        }

        foreach (var e in rule.Edges)
        {
            if (!byId.TryGetValue(e.Source, out var s) || !byId.TryGetValue(e.Target, out var t))
                continue;

            graph.Connections.Add(new ConnectionViewModel(s, t)
            {
                Id = e.Id,
                Stroke = BranchBrush(e.Branch),
                BranchLabel = e.Branch?.ToString().ToLowerInvariant(),
            });
        }

        return graph;
    }

    private static string CategoryLabel(NodeCategory c) => c switch
    {
        NodeCategory.RuleRef => "sub-rule",
        NodeCategory.GroupBy => "group by",
        NodeCategory.FilterList => "filter list",
        NodeCategory.TextParse => "parse",
        _ => c.ToString().ToLowerInvariant(),
    };

    // Light, muted category accents — final palette is a Phase-2 review item.
    private static Brush AccentFor(NodeCategory c)
    {
        var hex = c switch
        {
            NodeCategory.Input or NodeCategory.Output => "#64748B", // slate
            NodeCategory.Filter or NodeCategory.FilterList => "#2563EB", // blue
            NodeCategory.Logic => "#7C3AED", // violet
            NodeCategory.Product or NodeCategory.Constant => "#059669", // green
            NodeCategory.Mutator => "#D97706", // amber
            NodeCategory.Calc => "#0891B2", // cyan
            NodeCategory.Reference => "#4F46E5", // indigo
            NodeCategory.RuleRef => "#DB2777", // pink
            NodeCategory.Switch or NodeCategory.Assert or NodeCategory.Bucket => "#9333EA",
            NodeCategory.Iterator or NodeCategory.Merge => "#0D9488",
            _ => "#64748B",
        };
        return Freeze(hex);
    }

    private static Brush BranchBrush(EdgeBranch? b)
    {
        var hex = b switch
        {
            EdgeBranch.Pass => "#16A34A", // green
            EdgeBranch.Fail => "#DC2626", // red
            _ => "#94A3B8",               // slate (default / null)
        };
        return Freeze(hex);
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
