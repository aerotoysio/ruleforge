using Microsoft.Data.Sqlite;

namespace RuleForge.Core.Loader;

/// <summary>
/// The shared SQLite schema for a RuleForge <c>workspace.db</c>. Both the engine
/// (read-only here) and the editor (read/write, via Node's <c>node:sqlite</c>)
/// target this layout. Each row holds one JSON document — the same shape the
/// file workspace and DocumentForge already use — with a few columns lifted out
/// for indexing.
///
/// Engine-relevant tables (created here):
///   compiled_rules  — the COMPILED engine <see cref="Models.Rule"/> the runner
///                     executes (one row per id+version).
///   reference_sets  — lookup tables ({id,name,columns,rows,currentVersion}).
///
/// The editor adds its own authoring tables (rules, nodes, templates, assets,
/// samples, schema_templates, workspace) alongside these in the same file.
/// </summary>
public static class SqliteSchema
{
    public static string ConnStr(string dbPath) => $"Data Source={dbPath}";

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS compiled_rules (
            id        TEXT    NOT NULL,
            version   INTEGER NOT NULL,
            endpoint  TEXT    NOT NULL,
            method    TEXT    NOT NULL,
            status    TEXT,
            json      TEXT    NOT NULL,
            PRIMARY KEY (id, version)
        );
        CREATE INDEX IF NOT EXISTS idx_compiled_endpoint ON compiled_rules(endpoint, method, version);
        CREATE TABLE IF NOT EXISTS reference_sets (
            id        TEXT PRIMARY KEY,
            name      TEXT,
            version   INTEGER,
            json      TEXT NOT NULL
        );
        """;

    /// <summary>Open the db (creating the file if needed), set WAL, ensure tables exist.</summary>
    public static void EnsureCreated(string dbPath)
    {
        using var conn = new SqliteConnection(ConnStr(dbPath));
        conn.Open();
        // WAL lets the engine read while the editor writes — persists in the db header.
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }
}
