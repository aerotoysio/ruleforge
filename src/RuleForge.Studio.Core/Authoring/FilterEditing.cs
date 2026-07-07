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

    /// <summary>One condition of a (possibly multi-condition) filter on a single field.</summary>
    public sealed record FilterCondition(string Operator, IReadOnlyList<string> Values);

    /// <summary>Minimal builder used when seeding a brand-new filter node.</summary>
    public static StringFilterConfig BuildStringFilter(string path, string @operator, string value)
        => BuildStringFilter(path, @operator,
            string.IsNullOrEmpty(value) ? [] : [value],
            "any", "fail", caseInsensitive: true, trim: true);

    /// <summary>Single-condition builder (kept for the simple path).</summary>
    public static StringFilterConfig BuildStringFilter(
        string path, string @operator, IReadOnlyList<string> values,
        string arraySelector, string onMissing, bool caseInsensitive, bool trim)
        => BuildStringFilter(path, [new FilterCondition(@operator, values)], "all",
            arraySelector, onMissing, caseInsensitive, trim);

    /// <summary>
    /// Full builder: a stack of conditions on one field, combined by <paramref name="match"/>
    /// ("all" = AND, "any" = OR) — e.g. "in [EU countries] AND not_equals CH". One condition
    /// emits the engine's legacy single-Compare shape; several emit Conditions + Match.
    /// <paramref name="arraySelector"/> / <paramref name="onMissing"/> are engine JSON names.
    /// </summary>
    public static StringFilterConfig BuildStringFilter(
        string path, IReadOnlyList<FilterCondition> conditions, string match,
        string arraySelector, string onMissing, bool caseInsensitive, bool trim)
    {
        if (conditions.Count == 0)
            conditions = [new FilterCondition("equals", [])];

        var compares = conditions.Select(c => MakeCompare(c, caseInsensitive, trim)).ToList();

        return new StringFilterConfig(
            new StringFilterSource(SourceKind.Request, path),
            compares[0],
            ParseJsonEnum<ArraySelector>(arraySelector),
            ParseJsonEnum<OnMissing>(onMissing),
            Conditions: compares.Count > 1 ? compares : null,
            Match: compares.Count > 1 ? match : null);
    }

    /// <summary>The editable conditions of a config (Conditions when present, else the single Compare).</summary>
    public static IReadOnlyList<FilterCondition> ReadConditions(StringFilterConfig cfg)
    {
        var compares = cfg.Conditions is { Count: > 0 } cs ? cs : [cfg.Compare];
        return compares
            .Select(c => new FilterCondition(
                JsonName(c.Operator),
                c.Values?.ToList() ?? (c.Value is { } v ? new List<string> { v } : new List<string>())))
            .ToList();
    }

    private static StringFilterCompare MakeCompare(FilterCondition c, bool caseInsensitive, bool trim)
    {
        var op = ParseJsonEnum<StringFilterOperator>(c.Operator);
        return new StringFilterCompare(
            op,
            Value: !IsListOperator(c.Operator) && !IsUnaryOperator(c.Operator) && c.Values.Count > 0 ? c.Values[0] : null,
            Values: IsListOperator(c.Operator)
                ? c.Values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList()
                : null,
            CaseInsensitive: caseInsensitive,
            Trim: trim);
    }

    /// <summary>Plain-English one-liner for a filter config — shown on the node face.</summary>
    public static string Summarize(StringFilterConfig cfg)
    {
        var path = cfg.Source.Path ?? "?";

        var compares = cfg.Conditions is { Count: > 0 } cs ? cs : [cfg.Compare];
        var joiner = string.Equals(cfg.Match, "any", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
        var body = string.Join(joiner, compares.Select(DescribeCompare));

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

        return $"{selector}{path} {body}".Trim();
    }

    private static string DescribeCompare(StringFilterCompare c)
    {
        var op = JsonName(c.Operator).Replace('_', ' ');
        if (c.Operator is StringFilterOperator.IsNull or StringFilterOperator.IsEmpty)
            return op;
        if (c.Values is { Count: > 0 } vs)
        {
            var shown = vs.Count <= 4 ? string.Join(", ", vs) : string.Join(", ", vs.Take(3)) + $", +{vs.Count - 3}";
            return $"{op} [{shown}]";
        }
        return $"{op} '{c.Value}'";
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
