using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuleForge.Core.Models;

namespace RuleForge.Core.Loader;

/// <summary>
/// Engine-side data-plane source (see ARCHITECTURE.md). Keeps a LOCAL SQLite
/// replica in sync with the control plane's HTTP sync API, then serves reads
/// from that replica via an inner <see cref="SqliteRuleSource"/>. The request
/// hot path therefore never touches the network — only boot and
/// <see cref="RefreshAsync"/> pull. reference_sets + active api_keys are mirrored
/// into the same replica file so the existing <see cref="SqliteReferenceSetSource"/>
/// and SqliteApiKeyValidator work against it unchanged.
/// </summary>
public sealed class RemoteSyncRuleSource : IRuleSource, IDisposable
{
    private readonly RuleForgeSyncClient _sync;
    private readonly SqliteRuleSource _inner;

    public RemoteSyncRuleSource(string localDbPath, string syncBaseUrl, string? syncToken)
    {
        SqliteSchema.EnsureCreated(localDbPath);
        _sync = new RuleForgeSyncClient(localDbPath, syncBaseUrl, syncToken);
        // Best-effort initial pull. If the control plane is unreachable at boot,
        // serve from whatever is already in the local replica (stale, not down).
        try { _sync.SyncBlocking(); }
        catch (Exception e) { Console.Error.WriteLine($"[sync] initial sync failed, serving local replica: {e.Message}"); }
        _inner = new SqliteRuleSource(localDbPath);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _sync.SyncAsync(ct);     // best-effort; never throws
        await _inner.RefreshAsync(ct); // re-read bindings from the (now updated) replica
    }

    public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default)
        => _inner.GetByEndpointAsync(endpoint, method, ct);

    public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default)
        => _inner.GetByIdAsync(ruleId, version, ct);

    public Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default)
        => _inner.ListBindingsAsync(ct);

    /// <summary>The sync generation this engine has pulled — surfaced in the fleet heartbeat.</summary>
    public string LastGeneration => _sync.LastGeneration;

    public void Dispose() => _sync.Dispose();
}

/// <summary>
/// Pulls the control plane's sync API into a local SQLite replica. Delta by
/// manifest generation: immutable (id,version) rule + reference-set artifacts are
/// fetched only when missing; rows absent from the manifest are pruned; the
/// active api-key set is mirrored wholesale (so revocation drops keys here).
/// </summary>
public sealed class RuleForgeSyncClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _connStr;
    private string _lastGeneration = "";

    /// <summary>The manifest generation last successfully synced (for heartbeat freshness).</summary>
    public string LastGeneration => _lastGeneration;

    public RuleForgeSyncClient(string localDbPath, string baseUrl, string? token)
    {
        _connStr = SqliteSchema.ConnStr(localDbPath);
        _base = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(token)) _http.DefaultRequestHeaders.Add("X-Sync-Token", token);
    }

    public void SyncBlocking() => SyncAsync(default).GetAwaiter().GetResult();

    public async Task SyncAsync(CancellationToken ct = default)
    {
        try
        {
            var manifest = await GetJsonAsync<Manifest>($"{_base}/api/sync/manifest", ct);
            if (manifest is null || manifest.Generation == _lastGeneration) return; // unreachable or unchanged

            using var conn = new SqliteConnection(_connStr);
            conn.Open();

            await ReconcileRulesAsync(conn, manifest, ct);
            await ReconcileReferenceSetsAsync(conn, manifest, ct);
            await ReconcileApiKeysAsync(conn, ct);

            _lastGeneration = manifest.Generation;
        }
        catch (Exception e)
        {
            // Network blip / control plane down → keep the current replica. We do
            // NOT advance _lastGeneration, so the next tick retries.
            Console.Error.WriteLine($"[sync] sync failed (keeping local replica): {e.Message}");
        }
    }

    private async Task ReconcileRulesAsync(SqliteConnection conn, Manifest manifest, CancellationToken ct)
    {
        var want = new HashSet<(string, int)>(manifest.Rules.Select(r => (r.Id, r.Version)));
        var have = new HashSet<(string, int)>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT id, version FROM compiled_rules";
            using var rd = q.ExecuteReader();
            while (rd.Read()) have.Add((rd.GetString(0), rd.GetInt32(1)));
        }

        foreach (var r in manifest.Rules)
        {
            if (have.Contains((r.Id, r.Version))) continue; // immutable per version — already mirrored
            var art = await GetJsonAsync<RuleArtifact>($"{_base}/api/sync/rules/{Uri.EscapeDataString(r.Id)}/{r.Version}", ct);
            if (art is null) continue;
            using var up = conn.CreateCommand();
            up.CommandText = "INSERT OR REPLACE INTO compiled_rules (id, version, endpoint, method, status, json) VALUES ($id,$v,$e,$m,$s,$j)";
            up.Parameters.AddWithValue("$id", art.Id);
            up.Parameters.AddWithValue("$v", art.Version);
            up.Parameters.AddWithValue("$e", art.Endpoint ?? "");
            up.Parameters.AddWithValue("$m", art.Method ?? "POST");
            up.Parameters.AddWithValue("$s", (object?)art.Status ?? DBNull.Value);
            up.Parameters.AddWithValue("$j", art.Json ?? "");
            up.ExecuteNonQuery();
        }

        foreach (var (id, ver) in have)
        {
            if (want.Contains((id, ver))) continue; // pruned: unpublished/deleted upstream
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM compiled_rules WHERE id=$id AND version=$v";
            del.Parameters.AddWithValue("$id", id);
            del.Parameters.AddWithValue("$v", ver);
            del.ExecuteNonQuery();
        }
    }

    private async Task ReconcileReferenceSetsAsync(SqliteConnection conn, Manifest manifest, CancellationToken ct)
    {
        var want = new HashSet<string>(manifest.ReferenceSets.Select(r => r.Id));
        foreach (var rs in manifest.ReferenceSets)
        {
            var art = await GetJsonAsync<RefSetArtifact>($"{_base}/api/sync/reference-sets/{Uri.EscapeDataString(rs.Id)}", ct);
            if (art is null) continue;
            using var up = conn.CreateCommand();
            up.CommandText = "INSERT OR REPLACE INTO reference_sets (id, name, version, json) VALUES ($id,$n,$v,$j)";
            up.Parameters.AddWithValue("$id", art.Id);
            up.Parameters.AddWithValue("$n", (object?)art.Name ?? DBNull.Value);
            up.Parameters.AddWithValue("$v", art.Version);
            up.Parameters.AddWithValue("$j", art.Json ?? "");
            up.ExecuteNonQuery();
        }

        var have = new List<string>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT id FROM reference_sets";
            using var rd = q.ExecuteReader();
            while (rd.Read()) have.Add(rd.GetString(0));
        }
        foreach (var id in have)
        {
            if (want.Contains(id)) continue;
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM reference_sets WHERE id=$id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }
    }

    private async Task ReconcileApiKeysAsync(SqliteConnection conn, CancellationToken ct)
    {
        var resp = await GetJsonAsync<ApiKeysResponse>($"{_base}/api/sync/api-keys", ct);
        if (resp is null) return;
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM api_keys"; del.ExecuteNonQuery(); }
        foreach (var k in resp.Keys)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO api_keys (id, name, prefix, key_hash, created_by, created_at, last_used_at, revoked) VALUES ($id,NULL,$p,$h,NULL,NULL,NULL,0)";
            ins.Parameters.AddWithValue("$id", k.Id);
            ins.Parameters.AddWithValue("$p", (object?)k.Prefix ?? DBNull.Value);
            ins.Parameters.AddWithValue("$h", k.KeyHash);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public void Dispose() => _http.Dispose();

    private sealed record Manifest(string Generation, List<ManifestRule> Rules, List<ManifestRefSet> ReferenceSets, string KeysGeneration);
    private sealed record ManifestRule(string Id, int Version, string Endpoint, string Method, string? Status);
    private sealed record ManifestRefSet(string Id, int Version);
    private sealed record RuleArtifact(string Id, int Version, string? Endpoint, string? Method, string? Status, string? Json);
    private sealed record RefSetArtifact(string Id, string? Name, int Version, string? Json);
    private sealed record ApiKeysResponse(string KeysGeneration, List<ApiKeyRow> Keys);
    private sealed record ApiKeyRow(string Id, string? Prefix, string KeyHash);
}
