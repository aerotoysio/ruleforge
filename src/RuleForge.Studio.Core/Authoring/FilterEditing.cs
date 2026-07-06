using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Models;

namespace RuleForge.Studio.Core.Authoring;

/// <summary>
/// Read/build/write string-filter node configs. Everything round-trips through the engine's own
/// <see cref="AeroJson.Options"/>, so a config authored here deserializes byte-identically in
/// <c>RuleRunner</c>.
/// </summary>
public static class FilterEditing
{
    /// <summary>The operators offered for a string field, in JSON form (engine names).</summary>
    public static readonly IReadOnlyList<string> StringOperators = new[]
    {
        "equals", "not_equals", "contains", "not_contains",
        "starts_with", "ends_with", "in", "not_in", "regex", "is_null", "is_empty",
    };

    /// <summary>Operators whose right-hand side is a list of values.</summary>
    public static bool IsListOperator(string op) => op is "in" or "not_in";

    /// <summary>Operators that need no right-hand side at all.</summary>
    public static bool IsUnaryOperator(string op) => op is "is_null" or "is_empty";

    public static StringFilterConfig? ReadStringFilter(JsonElement? config)
    {
        if (config is null) return null;
        try { return config.Value.Deserialize<StringFilterConfig>(AeroJson.Options); }
        catch { return null; }
    }

    /// <summary>Minimal builder used when seeding a brand-new filter node.</summary>
    public static StringFilterConfig BuildStringFilter(string path, string @operator, string value)
        => BuildStringFilter(path, @operator,
            string.IsNullOrEmpty(value) ? [] : [value],
            "any", "fail", caseInsensitive: true, trim: true);

    /// <summary>
    /// Full builder. <paramref name="values"/> carries the RHS: one entry for scalar operators,
    /// many for in/not_in, none for is_null/is_empty. <paramref name="arraySelector"/> and
    /// <paramref name="onMissing"/> are engine JSON names ("any"/"all"/"none"/"first"/"only",
    /// "fail"/"pass"/"skip").
    /// </summary>
    public static StringFilterConfig BuildStringFilter(
        string path, string @operator, IReadOnlyList<string> values,
        string arraySelector, string onMissing, bool caseInsensitive, bool trim)
    {
        var op = ParseJsonEnum<StringFilterOperator>(@operator);

        var compare = new StringFilterCompare(
            op,
            Value: !IsListOperator(@operator) && !IsUnaryOperator(@operator) && values.Count > 0 ? values[0] : null,
            Values: IsListOperator(@operator)
                ? values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList()
                : null,
            CaseInsensitive: caseInsensitive,
            Trim: trim);

        return new StringFilterConfig(
            new StringFilterSource(SourceKind.Request, path),
            compare,
            ParseJsonEnum<ArraySelector>(arraySelector),
            ParseJsonEnum<OnMissing>(onMissing));
    }

    /// <summary>Plain-English one-liner for a filter config — shown on the node face.</summary>
    public static string Summarize(StringFilterConfig cfg)
    {
        var path = cfg.Source.Path ?? "?";
        var op = JsonName(cfg.Compare.Operator).Replace('_', ' ');

        var rhs = cfg.Compare.Values is { Count: > 0 } vs
            ? "[" + string.Join(", ", vs) + "]"
            : cfg.Compare.Operator is StringFilterOperator.IsNull or StringFilterOperator.IsEmpty
                ? ""
                : $"'{cfg.Compare.Value}'";

        var selector = path.Contains("[*]")
            ? cfg.ArraySelector switch
            {
                ArraySelector.Any => "any item: ",
                ArraySelector.All => "every item: ",
                ArraySelector.None => "no item: ",
                ArraySelector.First => "first item: ",
                ArraySelector.Only => "the only item: ",
                _ => "",
            }
            : "";

        return $"{selector}{path} {op} {rhs}".Trim();
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

    /// <summary>Update a node's label, description and config in one pass.</summary>
    public static Rule UpdateNode(Rule rule, string nodeId, string label, string? description, JsonElement config)
    {
        var nodes = rule.Nodes
            .Select(n => n.Id == nodeId
                ? n with
                {
                    Data = n.Data with
                    {
                        Label = string.IsNullOrWhiteSpace(label) ? n.Data.Label : label.Trim(),
                        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                        Config = config,
                    },
                }
                : n)
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

    public static string JsonName<T>(T value) where T : struct, Enum
        => JsonSerializer.Serialize(value, AeroJson.Options).Trim('"');

    private static T ParseJsonEnum<T>(string json) where T : struct, Enum
        => JsonSerializer.Deserialize<T>($"\"{json}\"", AeroJson.Options);
}
