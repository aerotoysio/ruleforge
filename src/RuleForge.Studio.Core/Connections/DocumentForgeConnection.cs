using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using RuleForge.DocumentForge;
using RuleForge.Studio.Core.Settings;

namespace RuleForge.Studio.Core.Connections;

/// <summary>
/// A connection to a DocumentForge instance — the central source of truth for rules and reference
/// sets. Reuses the engine's own DocumentForge client and sources, so Studio reads exactly what the
/// engine reads (rule headers in <c>rules</c>, published snapshots in <c>ruleversions</c>,
/// datasources in <c>referencesets</c>). NOTE: needs live verification against a running DF.
/// </summary>
public sealed class DocumentForgeConnection : IRuleForgeConnection, IDisposable
{
    private readonly HttpClient _http;
    private readonly DfClient _client;
    private readonly DocumentForgeRuleSource _ruleSource;
    private readonly DocumentForgeReferenceSetSource _refSource;
    private readonly string _prefix;

    public DocumentForgeConnection(ConnectionDescriptor descriptor, string? apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _client = new DfClient(_http, descriptor.Url!.TrimEnd('/'), apiKey ?? "");
        _prefix = descriptor.CollectionPrefix ?? "";
        _ruleSource = new DocumentForgeRuleSource(_client, descriptor.Environment ?? "staging", descriptor.CollectionPrefix);
        _refSource = new DocumentForgeReferenceSetSource(_client, descriptor.CollectionPrefix);
        DisplayName = string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.Url! : descriptor.Name;
    }

    public RuleForgeConnectionKind Kind => RuleForgeConnectionKind.DocumentForge;
    public string DisplayName { get; }

    public RuleForgeCapabilities Capabilities =>
        RuleForgeCapabilities.Rules | RuleForgeCapabilities.ReferenceSets |
        RuleForgeCapabilities.Environments | RuleForgeCapabilities.Publish;

    public IReferenceSetSource ReferenceSetSource => _refSource;

    public async Task ConnectAsync(CancellationToken ct = default)
        // A cheap query validates URL + key + collection prefix.
        => await _client.QueryAsync<RuleHeaderDoc>($"SELECT id FROM {_prefix}rules", ct);

    public async Task<IReadOnlyList<RuleSummary>> ListRulesAsync(CancellationToken ct = default)
    {
        var res = await _client.QueryAsync<RuleHeaderDoc>($"SELECT * FROM {_prefix}rules", ct);
        return res.Documents
            .Select(d => new RuleSummary(
                d.Id, d.Name ?? d.Id, d.Endpoint ?? "", d.Method ?? "", d.Status ?? "", d.CurrentVersion, d.Category))
            .ToList();
    }

    public Task<Rule?> GetRuleAsync(string ruleId, int? version = null, CancellationToken ct = default)
        => _ruleSource.GetByIdAsync(ruleId, version, ct);

    public async Task<IReadOnlyList<ReferenceSetSummary>> ListReferenceSetsAsync(CancellationToken ct = default)
    {
        var res = await _client.QueryAsync<RefHeaderDoc>($"SELECT id, name, columns FROM {_prefix}referencesets", ct);
        return res.Documents
            .Select(d => new ReferenceSetSummary(d.Id, d.Name ?? d.Id, d.Columns?.Count ?? 0, 0))
            .ToList();
    }

    public Task<ReferenceSet?> GetReferenceSetAsync(string id, CancellationToken ct = default)
        => _refSource.GetByIdAsync(id, ct);

    public void Dispose() => _http.Dispose();

    private sealed record RuleHeaderDoc(
        string Id, string? Name, string? Endpoint, string? Method, string? Status, int CurrentVersion, string? Category);

    private sealed record RefHeaderDoc(string Id, string? Name, List<string>? Columns);
}
