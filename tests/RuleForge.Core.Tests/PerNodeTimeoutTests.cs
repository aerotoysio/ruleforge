using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Per-node timeout via <c>Options.PerNodeTimeoutMs</c>. Async I/O nodes
/// (api / reference / mutator / sub-rule) honor the linked CT and abort.
/// CPU-bound nodes have their own bounds (calc timeout, refset cap), so
/// per-node timeout primarily covers async I/O.
/// </summary>
public class PerNodeTimeoutTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private sealed class DelayHandler : HttpMessageHandler
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
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

    private static RuleNode ApiNode(string id, string configJson) =>
        new(id, "api", new(0, 0), new(id, NodeCategory.Api, Config: Json(configJson)));

    [Fact]
    public async Task Per_node_timeout_fires_on_slow_api_node()
    {
        // Per-node 200ms < handler delay 2s < api node's own 30s timeout.
        // The per-node CTS should fire first, raising "exceeded per-node timeout".
        var handler = new DelayHandler { Delay = TimeSpan.FromSeconds(2) };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/x","method":"GET","timeoutMs":30000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, PerNodeTimeoutMs: 200, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("exceeded per-node timeout", err);
        Assert.Contains("200ms", err);
    }

    [Fact]
    public async Task Per_node_timeout_disabled_zero_does_not_wrap_with_cts()
    {
        // Setting PerNodeTimeoutMs=0 disables the wrapper. The api node's own
        // timeoutMs is then the only deadline.
        var handler = new DelayHandler { Delay = TimeSpan.FromMilliseconds(50) };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, PerNodeTimeoutMs: 0));

        Assert.Equal(Decision.Apply, env.Decision);
    }

    [Fact]
    public async Task Default_per_node_timeout_30s_allows_normal_evaluation()
    {
        // Sanity: 30s default is generous; a 10ms api call completes well within it.
        var handler = new DelayHandler { Delay = TimeSpan.FromMilliseconds(10) };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http));   // default PerNodeTimeoutMs=30000

        Assert.Equal(Decision.Apply, env.Decision);
    }

    [Fact]
    public async Task Per_node_timeout_classifies_as_PER_NODE_TIMEOUT_when_redacted()
    {
        var handler = new DelayHandler { Delay = TimeSpan.FromSeconds(2) };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/x","method":"GET","timeoutMs":30000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, PerNodeTimeoutMs: 200,
                Debug: true, RedactTraceErrors: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Equal("PER_NODE_TIMEOUT", err);
    }

    [Fact]
    public async Task Api_nodes_own_timeoutMs_still_works_under_per_node_wrapper()
    {
        // Api timeoutMs (200ms) < handler delay (2s) < per-node timeout (5s).
        // The api node's own timeout should fire first with API_NODE_ERROR
        // classification (raw: "request to ... timed out after 200ms").
        var handler = new DelayHandler { Delay = TimeSpan.FromSeconds(2) };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/x","method":"GET","timeoutMs":200}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, PerNodeTimeoutMs: 5000, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        // Either the api node's own timeout fires ("timed out after 200ms")
        // OR the per-node timeout if there's racing — both are acceptable
        // proof that timeouts work; api's is preferred.
        Assert.Contains("timed out", err);
    }
}
