using System.Text.Json;
using RuleForge.Core.Models;

namespace RuleForge.Studio.Core.Authoring;

/// <summary>
/// Structural edits to a rule graph — add/remove edges and nodes, persist positions. Everything
/// returns a new <see cref="Rule"/> (records), keeping the rule the single source of truth that the
/// canvas is re-projected from.
/// </summary>
public static class GraphEditing
{
    public static Rule AddEdge(Rule rule, string sourceId, EdgeBranch? branch, string targetId)
    {
        if (rule.Edges.Any(e => e.Source == sourceId && e.Target == targetId && e.Branch == branch))
            return rule;

        var id = $"e-{sourceId}-{branch?.ToString().ToLowerInvariant() ?? "def"}-{targetId}";
        var edges = rule.Edges.Append(new RuleEdge(id, sourceId, targetId, branch)).ToList();
        return rule with { Edges = edges };
    }

    public static Rule RemoveEdge(Rule rule, string sourceId, string targetId, EdgeBranch? branch)
    {
        var edges = rule.Edges
            .Where(e => !(e.Source == sourceId && e.Target == targetId && e.Branch == branch))
            .ToList();
        return rule with { Edges = edges };
    }

    public static (Rule Rule, string NodeId) AddNode(Rule rule, NodeCategory category, double x, double y)
    {
        var id = UniqueId(rule, Prefix(category));

        JsonElement? config = category == NodeCategory.Filter
            ? FilterEditing.ToConfig(FilterEditing.BuildStringFilter("$.", "equals", ""))
            : null;

        var node = new RuleNode(id, category.ToString().ToLowerInvariant(), new NodePosition(x, y),
            new NodeData(Label: DefaultLabel(category), Category: category, Config: config));

        return (rule with { Nodes = rule.Nodes.Append(node).ToList() }, id);
    }

    public static Rule SyncPositions(Rule rule, IReadOnlyDictionary<string, (double X, double Y)> positions)
    {
        var nodes = rule.Nodes
            .Select(n => positions.TryGetValue(n.Id, out var p) ? n with { Position = new NodePosition(p.X, p.Y) } : n)
            .ToList();
        return rule with { Nodes = nodes };
    }

    private static string UniqueId(Rule rule, string prefix)
    {
        var i = 1;
        string id;
        do { id = $"{prefix}-{i++}"; } while (rule.Nodes.Any(n => n.Id == id));
        return id;
    }

    private static string Prefix(NodeCategory c) => c switch
    {
        NodeCategory.Filter => "flt",
        NodeCategory.Logic => "logic",
        NodeCategory.Product => "prod",
        NodeCategory.Mutator => "mut",
        NodeCategory.Calc => "calc",
        NodeCategory.Constant => "const",
        NodeCategory.Output => "out",
        NodeCategory.Input => "in",
        _ => c.ToString().ToLowerInvariant(),
    };

    private static string DefaultLabel(NodeCategory c) => c switch
    {
        NodeCategory.Filter => "New filter",
        NodeCategory.Logic => "AND",
        NodeCategory.Product => "Product",
        NodeCategory.Mutator => "Mutator",
        NodeCategory.Calc => "Calc",
        NodeCategory.Constant => "Constant",
        NodeCategory.Output => "Output",
        _ => c.ToString(),
    };
}
