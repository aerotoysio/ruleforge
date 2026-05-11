using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Trace enrichment (#22): filter trace entries carry evaluatedSource /
/// evaluatedLiteral / operator / arraySelectorReason so the explainability
/// UI can render the actual comparison. Paired with sensitive-field
/// masking — fields tagged `sensitive: true` in inputSchema have their
/// resolved values redacted to "***" in the trace.
/// </summary>
public class TraceEnrichmentTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static Rule BuildRule(JsonElement inputSchema, params RuleNode[] middle)
    {
        var nodes = new List<RuleNode>
        {
            new("i", "input", new(0, 0), new("in", NodeCategory.Input)),
        };
        nodes.AddRange(middle);
        nodes.Add(new RuleNode("o", "output", new(0, 0), new("out", NodeCategory.Output)));
        var edges = new List<RuleEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
            edges.Add(new RuleEdge($"e{i}", nodes[i].Id, nodes[i + 1].Id, EdgeBranch.Default));
        return new Rule(
            "rule-test", "test", "/x", HttpMethodKind.POST,
            RuleStatus.Published, 1,
            inputSchema,
            JsonDocument.Parse("{}").RootElement,
            nodes, edges, "2026-04-27T00:00:00.000Z");
    }

    private static RuleNode StringFilter(string id, string sourcePath, string op, string compareValue) =>
        new(id, "filter", new(0, 0), new(id, NodeCategory.Filter, Config: Json($$"""
            {
              "source":        { "kind": "request", "path": "{{sourcePath}}" },
              "compare":       { "operator": "{{op}}", "value": "{{compareValue}}", "ignoreCase": false, "trim": false },
              "arraySelector": "first",
              "onMissing":     "fail"
            }
            """)));

    // ─── enrichment ────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_trace_includes_evaluated_source_and_operator()
    {
        var rule = BuildRule(Json("{}"),
            StringFilter("f", "$.country", "equals", "GB"));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"country":"GB"}"""),
            new RuleRunner.Options(Debug: true));

        Assert.Equal(Decision.Apply, env.Decision);
        var filterTrace = env.Trace!.First(t => t.NodeId == "f");
        Assert.NotNull(filterTrace.EvaluatedSource);
        Assert.Equal("GB", filterTrace.EvaluatedSource!.Value.GetString());
        Assert.Equal("equals", filterTrace.Operator);
        Assert.NotNull(filterTrace.EvaluatedLiteral);
        Assert.Equal("GB", filterTrace.EvaluatedLiteral!.Value.GetString());
    }

    [Fact]
    public async Task Filter_trace_shows_resolved_value_even_on_fail()
    {
        var rule = BuildRule(Json("{}"),
            StringFilter("f", "$.country", "equals", "GB"));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"country":"FR"}"""),
            new RuleRunner.Options(Debug: true));

        var filterTrace = env.Trace!.First(t => t.NodeId == "f");
        Assert.Equal(TraceOutcome.Fail, filterTrace.Outcome);
        Assert.Equal("FR", filterTrace.EvaluatedSource!.Value.GetString());
        Assert.Equal("GB", filterTrace.EvaluatedLiteral!.Value.GetString());
    }

    [Fact]
    public async Task Non_filter_nodes_have_no_enrichment_fields()
    {
        // Input and output nodes shouldn't populate evaluatedSource/Literal/etc.
        var rule = BuildRule(Json("{}"));   // no middle node — just input → output

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));

        var input = env.Trace!.First(t => t.NodeId == "i");
        Assert.Null(input.EvaluatedSource);
        Assert.Null(input.EvaluatedLiteral);
        Assert.Null(input.Operator);
    }

    // ─── sensitive-field masking ───────────────────────────────────────────

    private static JsonElement SchemaWithSensitive(string fieldName) => Json($$"""
        {
          "type": "object",
          "properties": {
            "{{fieldName}}": { "type": "string", "sensitive": true }
          }
        }
        """);

    [Fact]
    public async Task Sensitive_field_is_masked_in_evaluatedSource()
    {
        var rule = BuildRule(SchemaWithSensitive("pnr"),
            StringFilter("f", "$.pnr", "equals", "ABC123"));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"pnr":"ABC123"}"""),
            new RuleRunner.Options(Debug: true));

        var filterTrace = env.Trace!.First(t => t.NodeId == "f");
        // EvaluatedSource is masked
        Assert.Equal("***", filterTrace.EvaluatedSource!.Value.GetString());
        // EvaluatedLiteral is NOT masked (it's the rule's authored comparison value)
        Assert.Equal("ABC123", filterTrace.EvaluatedLiteral!.Value.GetString());
        // Verdict still works
        Assert.Equal(TraceOutcome.Pass, filterTrace.Outcome);
    }

    [Fact]
    public async Task Non_sensitive_field_in_same_schema_is_not_masked()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": {
                "pnr": { "type": "string", "sensitive": true },
                "country": { "type": "string" }
              }
            }
            """);
        var rule = BuildRule(schema,
            StringFilter("f", "$.country", "equals", "GB"));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"pnr":"X","country":"GB"}"""),
            new RuleRunner.Options(Debug: true));

        var filterTrace = env.Trace!.First(t => t.NodeId == "f");
        // country is not sensitive — value flows through
        Assert.Equal("GB", filterTrace.EvaluatedSource!.Value.GetString());
    }

    [Fact]
    public async Task Schema_without_sensitive_flags_does_not_pay_masking_cost()
    {
        // Sanity: trace works fine, no masking applied when no sensitive tags.
        var rule = BuildRule(Json("""{"type":"object"}"""),
            StringFilter("f", "$.x", "equals", "y"));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"x":"y"}"""),
            new RuleRunner.Options(Debug: true));

        var filterTrace = env.Trace!.First(t => t.NodeId == "f");
        Assert.Equal("y", filterTrace.EvaluatedSource!.Value.GetString());
    }
}
