using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Tier A composability fixes:
/// - #24 bucket.writeContext — chain bucket's chosen name into ctx so
///   downstream switch / filter / logic nodes can route on it via $ctx.X.
/// - #25 array transforms accept Source path — read the input array from
///   a JSONPath (request / ctx / frame) instead of upstream.
/// </summary>
public class ComposabilityFixesTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static Rule BuildLinearRule(params RuleNode[] middle)
    {
        var nodes = new List<RuleNode>();
        nodes.Add(new RuleNode("i", "input", new(0, 0), new("in", NodeCategory.Input)));
        nodes.AddRange(middle);
        nodes.Add(new RuleNode("o", "output", new(0, 0), new("out", NodeCategory.Output)));
        var edges = new List<RuleEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
            edges.Add(new RuleEdge($"e{i}", nodes[i].Id, nodes[i + 1].Id, EdgeBranch.Default));
        return new Rule(
            "rule-test", "test", "/x", HttpMethodKind.POST,
            RuleStatus.Published, 1,
            JsonDocument.Parse("{}").RootElement,
            JsonDocument.Parse("{}").RootElement,
            nodes, edges, "2026-04-27T00:00:00.000Z");
    }

    // ─── #24: bucket → switch chaining via ctx ─────────────────────────────

    [Fact]
    public async Task Bucket_writes_chosen_name_to_ctx_when_configured()
    {
        var rule = BuildLinearRule(
            new RuleNode("b", "bucket", new(0, 0), new("b", NodeCategory.Bucket, Config: Json("""
                { "hashKey": "$.pnr",
                  "writeContext": "arm",
                  "buckets": [{"name":"treatment","weight":50},{"name":"control","weight":50}] }
                """))),
            new RuleNode("s", "switch", new(0, 0), new("s", NodeCategory.Switch, Config: Json("""
                { "input": "$ctx.arm",
                  "cases": [
                    { "match": "treatment", "name": "new-rule" },
                    { "match": "control",   "name": "old-rule" }
                  ] }
                """))));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"pnr":"ABC123"}"""));
        Assert.Equal(Decision.Apply, env.Decision);

        // The switch downstream sees the bucket's choice via $ctx.arm and
        // routes accordingly. The result is the switch's matched case name.
        var picked = env.Result!.Value.GetString();
        Assert.True(picked == "new-rule" || picked == "old-rule",
            $"expected one of new-rule/old-rule, got '{picked}'");
    }

    [Fact]
    public async Task Bucket_without_writeContext_does_not_mutate_ctx()
    {
        // No writeContext set — ctx stays empty, switch can't read it,
        // switch errors (no case matched + no default).
        var rule = BuildLinearRule(
            new RuleNode("b", "bucket", new(0, 0), new("b", NodeCategory.Bucket, Config: Json("""
                { "hashKey": "$.pnr",
                  "buckets": [{"name":"a","weight":100}] }
                """))),
            new RuleNode("s", "switch", new(0, 0), new("s", NodeCategory.Switch, Config: Json("""
                { "input": "$ctx.arm",
                  "cases": [{ "match": "a", "name": "matched" }],
                  "default": "no-arm" }
                """))));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"pnr":"ABC"}"""));
        // ctx.arm was never set, switch falls to default
        Assert.Equal("no-arm", env.Result!.Value.GetString());
    }

    // ─── #25: array transforms with source: $.path ─────────────────────────

    private static RuleNode ConstantNode(string id, string configJson) =>
        new(id, "constant", new(0, 0), new(id, NodeCategory.Constant, Config: Json(configJson)));

    [Fact]
    public async Task Sort_source_path_reads_from_request_instead_of_upstream()
    {
        // No upstream array — sort reads directly from request via source.
        var rule = BuildLinearRule(
            new RuleNode("s", "sort", new(0, 0), new("s", NodeCategory.Sort, Config: Json("""
                { "source": "$.quotes", "sortKey": "$.price", "direction": "asc" }
                """))));

        var env = await new RuleRunner().RunAsync(rule,
            Json("""{"quotes":[{"price":300},{"price":100},{"price":200}]}"""));

        var prices = env.Result!.Value.EnumerateArray()
            .Select(x => x.GetProperty("price").GetInt32()).ToArray();
        Assert.Equal(new[] { 100, 200, 300 }, prices);
    }

    [Fact]
    public async Task Sort_then_limit_top_n_pattern_via_source_paths()
    {
        // Cheapest 3 — 4-node rule instead of the 6-node iterator+merge workaround.
        var rule = BuildLinearRule(
            new RuleNode("s", "sort", new(0, 0), new("s", NodeCategory.Sort, Config: Json("""
                { "source": "$.quotes", "sortKey": "$.price" }
                """))),
            new RuleNode("l", "limit", new(0, 0), new("l", NodeCategory.Limit, Config: Json("""
                { "count": 3 }
                """))));

        var env = await new RuleRunner().RunAsync(rule,
            Json("""{"quotes":[{"price":300},{"price":100},{"price":200},{"price":50},{"price":400}]}"""));

        var prices = env.Result!.Value.EnumerateArray()
            .Select(x => x.GetProperty("price").GetInt32()).ToArray();
        Assert.Equal(new[] { 50, 100, 200 }, prices);
    }

    [Fact]
    public async Task Distinct_source_path_dedups_request_array()
    {
        var rule = BuildLinearRule(
            new RuleNode("d", "distinct", new(0, 0), new("d", NodeCategory.Distinct, Config: Json("""
                { "source": "$.tags" }
                """))));

        var env = await new RuleRunner().RunAsync(rule,
            Json("""{"tags":[1,2,2,3,1,3]}"""));

        var arr = env.Result!.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, arr);
    }

    [Fact]
    public async Task GroupBy_source_path_partitions_request_array()
    {
        var rule = BuildLinearRule(
            new RuleNode("g", "groupBy", new(0, 0), new("g", NodeCategory.GroupBy, Config: Json("""
                { "source": "$.pax", "groupKey": "$.type" }
                """))));

        var env = await new RuleRunner().RunAsync(rule,
            Json("""{"pax":[{"id":"p1","type":"ADT"},{"id":"p2","type":"CHD"},{"id":"p3","type":"ADT"}]}"""));

        var groups = env.Result!.Value;
        Assert.Equal(2, groups.GetProperty("ADT").GetArrayLength());
        Assert.Equal(1, groups.GetProperty("CHD").GetArrayLength());
    }

    [Fact]
    public async Task Source_path_resolving_to_non_array_yields_error()
    {
        var rule = BuildLinearRule(
            new RuleNode("s", "sort", new(0, 0), new("s", NodeCategory.Sort, Config: Json("""
                { "source": "$.price" }
                """))));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"price":42}"""),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("not a JSON array", err);
    }

    [Fact]
    public async Task Source_path_missing_yields_error()
    {
        var rule = BuildLinearRule(
            new RuleNode("s", "sort", new(0, 0), new("s", NodeCategory.Sort, Config: Json("""
                { "source": "$.missing" }
                """))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("did not resolve", err);
    }

    [Fact]
    public async Task Backwards_compat_upstream_read_still_works_when_source_absent()
    {
        // Original semantics — array comes from upstream (constant node here).
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[3,1,2]}"""),
            new RuleNode("s", "sort", new(0, 0), new("s", NodeCategory.Sort, Config: Json("""{}"""))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var arr = env.Result!.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, arr);
    }
}
