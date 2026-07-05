using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;

namespace RuleForge.Studio.Core.Testing;

/// <summary>
/// Evaluates a rule in-process using the engine's own <see cref="RuleRunner"/> — no service,
/// no CLI shell-out. This is the backbone of Studio's test harness: the same code path the
/// production engine runs, so a rule that passes here passes in production.
/// </summary>
public sealed class InProcessEvaluator
{
    private readonly IReferenceSetSource? _referenceSets;
    private readonly IRuleSource? _subRules;

    public InProcessEvaluator(IReferenceSetSource? referenceSets = null, IRuleSource? subRules = null)
    {
        _referenceSets = referenceSets;
        _subRules = subRules;
    }

    /// <summary>Run <paramref name="rule"/> against a request payload; trace is on by default.</summary>
    public Envelope Evaluate(Rule rule, JsonElement request, bool debug = true)
    {
        var options = new RuleRunner.Options(
            Debug: debug,
            ReferenceSetSource: _referenceSets,
            SubRuleSource: _subRules);

        return new RuleRunner()
            .RunAsync(rule, request, options)
            .GetAwaiter()
            .GetResult();
    }
}
