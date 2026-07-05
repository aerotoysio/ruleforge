using RuleForge.Core.Loader;
using RuleForge.Core.Models;

namespace RuleForge.Studio.Core.Connections;

public enum RuleForgeConnectionKind
{
    /// <summary>Rules + reference sets read straight from a local folder/workspace.</summary>
    LocalWorkspace,

    /// <summary>Rules + reference sets held in a DocumentForge instance (the source of truth).</summary>
    DocumentForge,
}

/// <summary>
/// What a connection can do. The UI greys out features a connection doesn't support — the same
/// capability-flag pattern DocumentForge Studio uses.
/// </summary>
[Flags]
public enum RuleForgeCapabilities
{
    None = 0,
    Rules = 1 << 0,
    ReferenceSets = 1 << 1,
    Environments = 1 << 2,
    Publish = 1 << 3,     // write / publish versions back to the store
    LiveEngine = 1 << 4,  // /admin + eval against a running RuleForge.Api
}

public sealed record RuleSummary(
    string Id, string Name, string Endpoint, string Method, string Status, int Version, string? Category);

public sealed record ReferenceSetSummary(string Id, string Name, int ColumnCount, int RowCount);

/// <summary>
/// Transport-neutral view of a RuleForge rule store. Every Studio screen works against this
/// interface, so the same UI drives a local folder OR a DocumentForge instance (and, later, a
/// live engine). Mirrors DocumentForge Studio's <c>IDfConnection</c>.
/// </summary>
public interface IRuleForgeConnection
{
    RuleForgeConnectionKind Kind { get; }
    string DisplayName { get; }
    RuleForgeCapabilities Capabilities { get; }

    Task<IReadOnlyList<RuleSummary>> ListRulesAsync(CancellationToken ct = default);
    Task<Rule?> GetRuleAsync(string ruleId, int? version = null, CancellationToken ct = default);

    Task<IReadOnlyList<ReferenceSetSummary>> ListReferenceSetsAsync(CancellationToken ct = default);
    Task<ReferenceSet?> GetReferenceSetAsync(string id, CancellationToken ct = default);

    /// <summary>Reference-set source wired into in-process evaluation (mutator lookups, reference nodes).</summary>
    IReferenceSetSource ReferenceSetSource { get; }
}
