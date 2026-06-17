using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RuleForge.Api;

/// <summary>
/// Validates an incoming X-AERO-Key against the <c>api_keys</c> table in the
/// shared <c>workspace.db</c>. Keys are minted (and revoked) in the editor;
/// only a SHA-256 hash is stored, so we hash the supplied key and look it up.
/// This is the engine half of the "gold sync": a key created in the editor is
/// accepted here immediately, and revoking it there rejects it here immediately.
/// </summary>
public interface IApiKeyValidator
{
    /// <summary>True if any non-revoked key exists — i.e. key enforcement is on.</summary>
    bool HasAnyKeys();

    /// <summary>True if the supplied raw key matches a non-revoked stored key.</summary>
    bool IsValid(string suppliedKey);
}

/// <summary>No-op validator used when the engine isn't backed by a workspace.db.</summary>
public sealed class NullApiKeyValidator : IApiKeyValidator
{
    public bool HasAnyKeys() => false;
    public bool IsValid(string suppliedKey) => false;
}

/// <summary>Reads editor-minted keys from the shared workspace.db.</summary>
public sealed class SqliteApiKeyValidator : IApiKeyValidator
{
    private readonly string _connStr;

    public SqliteApiKeyValidator(string dbPath)
    {
        // Pooling=False forces a fresh connection per check. A pooled connection
        // caches its WAL read snapshot and misses another process's writes — so
        // a key revoked in the editor would keep working here. A fresh connection
        // always reads the latest committed state, which is exactly what a
        // security check (especially revocation) requires. The cost — one
        // connection open per validation — is outside the engine's measured
        // compute time (DurationMicros wraps rule evaluation only).
        _connStr = $"Data Source={dbPath};Pooling=False";
    }

    public bool HasAnyKeys()
    {
        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM api_keys WHERE revoked = 0";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            // Table may not exist yet (no keys minted). Treat as "no keys" so the
            // engine stays open until the first key is created in the editor.
            return false;
        }
    }

    public bool IsValid(string suppliedKey)
    {
        if (string.IsNullOrEmpty(suppliedKey)) return false;
        var hash = Sha256Hex(suppliedKey);
        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM api_keys WHERE key_hash = $h AND revoked = 0 LIMIT 1";
            cmd.Parameters.AddWithValue("$h", hash);
            if (cmd.ExecuteScalar() is not string id) return false;
            TouchLastUsed(conn, id);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TouchLastUsed(SqliteConnection conn, string id)
    {
        try
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE api_keys SET last_used_at = $t WHERE id = $id";
            upd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
        catch { /* best-effort; never fail the request over a timestamp */ }
    }

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
