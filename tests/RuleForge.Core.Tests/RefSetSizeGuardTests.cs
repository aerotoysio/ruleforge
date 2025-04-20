using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Cap on reference-set row count via <c>Options.MaxReferenceSetRows</c>
/// (default 100k). A misconfigured / oversize refset would otherwise
/// memory-pressure the pod and slow the per-row scan loop linearly.
/// Applies to both `reference` nodes and `mutator` lookup-and-replace.
/// </summary>
public class RefSetSizeGuardTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private sealed class InMemoryRefSetSource : IReferenceSetSource
    {
        private readonly Dictionary<string, ReferenceSet> _sets = new();
        public InMemoryRefSetSource Add(ReferenceSet rs) { _sets[rs.Id] = rs; return this; }
        public Task<ReferenceSet?> GetByIdAsync(string referenceId, CancellationToken ct = default) =>
            Task.FromResult(_sets.GetValueOrDefault(referenceId));
    }

    private static ReferenceSet MakeRefSet(string id, int rowCount)
    {
        var rows = new List<IReadOnlyDictionary<string, JsonElement>>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(new Dictionary<string, JsonElement>
            {
                ["code"] = Json($"\"R{i}\""),
                ["value"] = Json($"{i}"),
            });
        }
        return new ReferenceSet(id, id, new[] { "code", "value" }, rows, 1);
    }

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

    private static RuleNode RefNode(string id, string refId) =>
        new(id, "reference", new(0, 0), new(id, NodeCategory.Reference,
            Config: Json("{\"referenceId\":\"" + refId + "\",\"matchOn\":{}}")));

    [Fact]
    public async Task RefSet_within_limit_succeeds()
    {
        var refs = new InMemoryRefSetSource().Add(MakeRefSet("rs-small", rowCount: 100));
        var rule = BuildLinearRule(RefNode("r", "rs-small"));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(ReferenceSetSource: refs, MaxReferenceSetRows: 200));

        Assert.Equal(Decision.Apply, env.Decision);
    }

    [Fact]
    public async Task Reference_node_with_oversize_refset_yields_error()
    {
        // 1500 rows, limit set to 1000.
        var refs = new InMemoryRefSetSource().Add(MakeRefSet("rs-big", rowCount: 1500));
        var rule = BuildLinearRule(RefNode("r", "rs-big"));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(ReferenceSetSource: refs, MaxReferenceSetRows: 1000, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("1500 rows", err);
        Assert.Contains("exceeds MaxReferenceSetRows", err);
    }

    [Fact]
    public async Task Mutator_lookup_replace_with_oversize_refset_yields_error()
    {
        var refs = new InMemoryRefSetSource().Add(MakeRefSet("rs-big", rowCount: 1500));

        // A mutator that does lookup-and-replace against the oversize refset.
        var product = new RuleNode("p", "product", new(0, 0), new("p", NodeCategory.Product,
            Config: Json("""{"output":{"code":"R0","value":0}}""")));
        var mutator = new RuleNode("m", "mutator", new(0, 0), new("m", NodeCategory.Mutator,
            Config: Json("""
                {
                  "target": "value",
                  "lookup": {
                    "referenceId": "rs-big",
                    "matchOn": { "code": "$.code" },
                    "returnColumn": "value"
                  }
                }
                """)));

        var rule = BuildLinearRule(product, mutator);

        var env = await new RuleRunner().RunAsync(rule, Json("""{"code":"R0"}"""),
            new RuleRunner.Options(ReferenceSetSource: refs, MaxReferenceSetRows: 1000, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("1500 rows", err);
    }

    [Fact]
    public async Task Default_limit_100k_allows_realistic_refset_sizes()
    {
        // 5000 rows is well within default 100k — verifies default isn't tight.
        var refs = new InMemoryRefSetSource().Add(MakeRefSet("rs-realistic", rowCount: 5000));
        var rule = BuildLinearRule(RefNode("r", "rs-realistic"));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(ReferenceSetSource: refs));   // default MaxReferenceSetRows

        Assert.Equal(Decision.Apply, env.Decision);
    }
}
