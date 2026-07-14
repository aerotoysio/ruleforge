using System.Text.Json.Nodes;
using RuleForge.DocumentForge;

namespace RuleForge.Admin;

/// <summary>
/// DocumentForge-backed store for Policy Studio documents.
///
/// Deliberately schema-tolerant: documents are JsonObjects, not typed records.
/// The Admin layer is a pass-through store for shapes owned by the TypeScript
/// client (content is TipTap JSON, statements embed schema fields); the server
/// only reads the handful of fields it computes on (id, status, version,
/// statements). Typed models live where the server actually evaluates —
/// see <see cref="TestRunner"/>.
/// </summary>
public sealed class AdminStore
{
    private readonly DfClient _df;
    private readonly string _prefix;

    public AdminStore(DfClient df, string collectionPrefix)
    {
        _df = df;
        _prefix = collectionPrefix;
    }

    public string Col(string name) => _prefix + name;

    public static string Escape(string s) => s.Replace("'", "''");

    public static string NewId(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    public async Task<List<JsonObject>> ListAsync(string collection, string? where = null, CancellationToken ct = default)
    {
        var sql = $"SELECT * FROM {Col(collection)}" + (where is null ? "" : $" WHERE {where}");
        var docs = await QueryOrEmptyAsync(sql, ct);
        return docs.Select(Clean).ToList();
    }

    /// <summary>
    /// DF collections materialize on first insert; querying one that doesn't
    /// exist yet is a 400, which the store treats as an empty result.
    /// </summary>
    private async Task<IReadOnlyList<JsonObject>> QueryOrEmptyAsync(string sql, CancellationToken ct)
    {
        try
        {
            return (await _df.QueryAsync<JsonObject>(sql, ct)).Documents;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                              && ex.Message.Contains("Collection", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<JsonObject>();
        }
    }

    public async Task<JsonObject?> GetByIdAsync(string collection, string id, CancellationToken ct = default)
    {
        var docs = await QueryOrEmptyAsync(
            $"SELECT * FROM {Col(collection)} WHERE id = '{Escape(id)}'", ct);
        return docs.Count == 0 ? null : Clean(docs[0]);
    }

    public async Task<JsonObject> InsertAsync(string collection, JsonObject doc, string idPrefix, CancellationToken ct = default)
    {
        if (doc["id"] is null) doc["id"] = NewId(idPrefix);
        var payload = Strip(doc);
        await _df.InsertAsync(Col(collection), payload, ct);
        return payload;
    }

    /// <summary>Replace the document whose domain <c>id</c> matches. Returns null when absent.</summary>
    public async Task<JsonObject?> ReplaceByIdAsync(string collection, string id, JsonObject doc, CancellationToken ct = default)
    {
        var dfId = await GetDfIdAsync(collection, id, ct);
        if (dfId is null) return null;
        doc["id"] = id;
        var payload = Strip(doc);
        await _df.ReplaceAsync(Col(collection), dfId, payload, ct);
        return payload;
    }

    public async Task<bool> DeleteByIdAsync(string collection, string id, CancellationToken ct = default)
    {
        var dfId = await GetDfIdAsync(collection, id, ct);
        if (dfId is null) return false;
        await _df.DeleteAsync(Col(collection), dfId, ct);
        return true;
    }

    public async Task<int> DeleteWhereAsync(string collection, string where, CancellationToken ct = default)
    {
        var docs = await QueryOrEmptyAsync($"SELECT * FROM {Col(collection)} WHERE {where}", ct);
        foreach (var doc in docs)
        {
            var dfId = doc["_id"]?.GetValue<string>();
            if (dfId is not null) await _df.DeleteAsync(Col(collection), dfId, ct);
        }
        return docs.Count;
    }

    public async Task<int> CountAsync(string collection, CancellationToken ct = default)
    {
        var docs = await QueryOrEmptyAsync($"SELECT * FROM {Col(collection)}", ct);
        return docs.Count;
    }

    private async Task<string?> GetDfIdAsync(string collection, string id, CancellationToken ct)
    {
        var docs = await QueryOrEmptyAsync(
            $"SELECT * FROM {Col(collection)} WHERE id = '{Escape(id)}'", ct);
        return docs.Count == 0 ? null : docs[0]["_id"]?.GetValue<string>();
    }

    /// <summary>Deep-clone without DF bookkeeping fields — safe to return or re-insert.</summary>
    private static JsonObject Strip(JsonObject doc)
    {
        var clone = (JsonObject)doc.DeepClone();
        clone.Remove("_id");
        clone.Remove("_etag");
        return clone;
    }

    private static JsonObject Clean(JsonObject doc) => Strip(doc);
}
