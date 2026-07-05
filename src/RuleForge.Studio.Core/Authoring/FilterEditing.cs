using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Models;

namespace RuleForge.Studio.Core.Authoring;

/// <summary>
/// Read/build/write string-filter node configs, and place a new filter on the graph. Everything
/// round-trips through the engine's own <see cref="AeroJson.Options"/>, so a config authored here
/// deserializes byte-identically in <c>RuleRunner</c>.
/// </summary>
public static class FilterEditing
{
    /// <summary>The operators offered for a string field, in JSON form (engine names).</summary>
    public static readonly IReadOnlyList<string> StringOperators = new[]
    {
        "equals", "not_equals", "contains", "not_contains",
        "starts_with", "ends_with", "in", "not_in", "is_null", "is_empty",
    };

    public static StringFilterConfig? ReadStringFilter(JsonElement? config)
    {
        if (config is null) return null;
        try { return config.Value.Deserialize<StringFilterConfig>(AeroJson.Options); }
        catch { return null; }
    }

    public static StringFilterConfig BuildStringFilter(string path, string @operator, string value)
    {
        var op = ParseOperator(@operator);
        var isList = op is StringFilterOperator.In or StringFilterOperator.NotIn;
        var isUnary = op is StringFilterOperator.IsNull or StringFilterOperator.IsEmpty;

        var compare = new StringFilterCompare(
            op,
            Value: isList || isUnary ? null : value,
            Values: isList
                ? value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList()
                : null,
            CaseInsensitive: true,
            Trim: true);

        var selector = path.Contains("[*]") ? ArraySelector.Any : ArraySelector.First;
        return new StringFilterConfig(new StringFilterSource(SourceKind.Request, path), compare, selector, OnMissing.Fail);
    }

    public static JsonElement ToConfig(StringFilterConfig config)
    {
        var json = JsonSerializer.Serialize(config, AeroJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static Rule ReplaceNodeConfig(Rule rule, string nodeId, JsonElement config)
    {
        var nodes = rule.Nodes
            .Select(n => n.Id == nodeId ? n with { Data = n.Data with { Config = config } } : n)
            .ToList();
        return rule with { Nodes = nodes };
    }

    /// <summary>Add a string-filter node (default: first string field, equals) wired from input.</summary>
    public static (Rule Rule, string NodeId) AddStringFilter(Rule rule, string defaultPath)
    {
        var input = rule.Nodes.FirstOrDefault(n => n.Data.Category == NodeCategory.Input);
        var id = $"flt-{rule.Nodes.Count + 1}";

        var pos = input is null
            ? new NodePosition(120, 120)
            : new NodePosition(input.Position.X + 260, input.Position.Y + 140);

        var cfg = ToConfig(BuildStringFilter(defaultPath, "equals", ""));
        var node = new RuleNode(id, "filter", pos,
            new NodeData(Label: "New filter", Category: NodeCategory.Filter, Config: cfg));

        var nodes = rule.Nodes.Append(node).ToList();
        var edges = rule.Edges.ToList();
        if (input is not null)
            edges.Add(new RuleEdge($"e-{id}", input.Id, id, EdgeBranch.Default));

        return (rule with { Nodes = nodes, Edges = edges }, id);
    }

    private static StringFilterOperator ParseOperator(string json)
        => JsonSerializer.Deserialize<StringFilterOperator>($"\"{json}\"", AeroJson.Options);
}
