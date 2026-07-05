using RuleForge.Core.Loader;
using RuleForge.Core.Models;

namespace RuleForge.Studio.Core.Connections;

/// <summary>
/// A connection backed by a local folder of rule + reference-set JSON (the engine's "local" mode).
/// Useful for offline authoring and as the reference implementation while the DocumentForge
/// connection is built out. Rules and reference sets are read through the engine's own loaders,
/// so what Studio sees is exactly what the engine would load.
/// </summary>
public sealed class LocalWorkspaceConnection : IRuleForgeConnection
{
    private readonly string _rulesDir;
    private readonly string _refsDir;
    private readonly LocalFileRuleSource _rules;
    private readonly LocalFileReferenceSetSource _refs;

    public LocalWorkspaceConnection(string rulesDir, string refsDir, string? displayName = null)
    {
        _rulesDir = rulesDir;
        _refsDir = refsDir;
        _rules = new LocalFileRuleSource(rulesDir);
        _refs = new LocalFileReferenceSetSource(refsDir);
        DisplayName = displayName ?? "Local workspace";
    }

    public RuleForgeConnectionKind Kind => RuleForgeConnectionKind.LocalWorkspace;
    public string DisplayName { get; }
    public RuleForgeCapabilities Capabilities => RuleForgeCapabilities.Rules | RuleForgeCapabilities.ReferenceSets;
    public IReferenceSetSource ReferenceSetSource => _refs;

    public async Task<IReadOnlyList<RuleSummary>> ListRulesAsync(CancellationToken ct = default)
    {
        var list = new List<RuleSummary>();
        foreach (var id in DiscoverRuleIds(_rulesDir))
        {
            var r = await _rules.GetByIdAsync(id, null, ct);
            if (r is not null)
                list.Add(new RuleSummary(
                    r.Id, r.Name, r.Endpoint, r.Method.ToString(), r.Status.ToString(), r.CurrentVersion, r.Category));
        }
        return list;
    }

    public Task<Rule?> GetRuleAsync(string ruleId, int? version = null, CancellationToken ct = default)
        => _rules.GetByIdAsync(ruleId, version, ct);

    public async Task<IReadOnlyList<ReferenceSetSummary>> ListReferenceSetsAsync(CancellationToken ct = default)
    {
        var list = new List<ReferenceSetSummary>();
        if (Directory.Exists(_refsDir))
        {
            foreach (var f in Directory.EnumerateFiles(_refsDir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                var rs = await _refs.GetByIdAsync(id, ct);
                if (rs is not null)
                    list.Add(new ReferenceSetSummary(rs.Id, rs.Name, rs.Columns.Count, rs.Rows.Count));
            }
        }
        return list;
    }

    public Task<ReferenceSet?> GetReferenceSetAsync(string id, CancellationToken ct = default)
        => _refs.GetByIdAsync(id, ct);

    private static IEnumerable<string> DiscoverRuleIds(string rulesDir)
    {
        if (!Directory.Exists(rulesDir)) return [];
        return Directory.EnumerateFiles(rulesDir, "*.v*.json")
            .Select(f => Path.GetFileName(f)!)
            .Select(name =>
            {
                var i = name.IndexOf(".v", StringComparison.Ordinal);
                return i > 0 ? name[..i] : name;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
    }
}
