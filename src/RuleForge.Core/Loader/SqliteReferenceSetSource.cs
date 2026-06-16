using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RuleForge.Core.Loader;

/// <summary>
/// Reads reference sets from a shared SQLite <c>workspace.db</c> (the
/// <c>reference_sets</c> table; row JSON shape
/// <c>{"id","name","columns","rows","currentVersion"}</c>). Mirrors
/// <see cref="LocalFileReferenceSetSource"/> — caches aggressively (reference
/// sets are immutable per version) and drops the cache on <see cref="RefreshAsync"/>.
/// </summary>
public sealed class SqliteReferenceSetSource : IReferenceSetSource
{
    private static readonly JsonSerializerOptions JsonOptions = AeroJson.Options;

    private readonly string _connStr;
    private ConcurrentDictionary<string, ReferenceSet> _cache = new();

    public SqliteReferenceSetSource(string dbPath)
    {
        SqliteSchema.EnsureCreated(dbPath);
        _connStr = SqliteSchema.ConnStr(dbPath);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        _cache = new ConcurrentDictionary<string, ReferenceSet>();
        return Task.CompletedTask;
    }

    public Task<ReferenceSet?> GetByIdAsync(string referenceId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(referenceId, out var cached)) return Task.FromResult<ReferenceSet?>(cached);

        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM reference_sets WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", referenceId);
        if (cmd.ExecuteScalar() is not string json) return Task.FromResult<ReferenceSet?>(null);

        var doc = JsonSerializer.Deserialize<RawRefSet>(json, JsonOptions)
                  ?? throw new InvalidOperationException($"reference set {referenceId} parsed to null");
        var rows = doc.Rows.Select(r => (IReadOnlyDictionary<string, JsonElement>)r).ToList();
        var refSet = new ReferenceSet(doc.Id, doc.Name, doc.Columns, rows, doc.CurrentVersion);
        _cache[referenceId] = refSet;
        return Task.FromResult<ReferenceSet?>(refSet);
    }

    private sealed class RawRefSet
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
        public List<Dictionary<string, JsonElement>> Rows { get; set; } = new();
        public int CurrentVersion { get; set; } = 1;
    }
}
