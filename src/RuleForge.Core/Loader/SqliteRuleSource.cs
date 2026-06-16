using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuleForge.Core.Models;

namespace RuleForge.Core.Loader;

/// <summary>
/// Reads compiled engine rules from a shared SQLite <c>workspace.db</c>
/// (the <c>compiled_rules</c> table). Mirrors <see cref="LocalFileRuleSource"/>:
/// an endpoint→rule binding map is cached (refreshable), while rule bodies are
/// read per call so content edits are picked up immediately. New or re-pointed
/// endpoints are picked up on <see cref="RefreshAsync"/> — which the editor
/// fires (POST /admin/refresh) when it saves.
/// </summary>
public sealed class SqliteRuleSource : IRuleSource
{
    private static readonly JsonSerializerOptions JsonOptions = AeroJson.Options;

    private readonly string _connStr;
    private Dictionary<string, (string id, int version)> _bindings;

    public SqliteRuleSource(string dbPath)
    {
        SqliteSchema.EnsureCreated(dbPath);
        _connStr = SqliteSchema.ConnStr(dbPath);
        _bindings = LoadBindings(_connStr);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        _bindings = LoadBindings(_connStr);
        return Task.CompletedTask;
    }

    private static Dictionary<string, (string, int)> LoadBindings(string connStr)
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Ascending so the highest version wins per "{METHOD} {endpoint}" key.
        cmd.CommandText = "SELECT endpoint, method, id, version FROM compiled_rules ORDER BY version ASC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            map[$"{rd.GetString(1)} {rd.GetString(0)}"] = (rd.GetString(2), rd.GetInt32(3));
        return map;
    }

    public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default)
    {
        if (!_bindings.TryGetValue($"{method} {endpoint}", out var b))
            return Task.FromResult<Rule?>(null);
        return GetByIdAsync(b.id, b.version, ct);
    }

    public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (version is int v)
        {
            cmd.CommandText = "SELECT json FROM compiled_rules WHERE id = $id AND version = $v LIMIT 1";
            cmd.Parameters.AddWithValue("$id", ruleId);
            cmd.Parameters.AddWithValue("$v", v);
        }
        else
        {
            cmd.CommandText = "SELECT json FROM compiled_rules WHERE id = $id ORDER BY version DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$id", ruleId);
        }
        if (cmd.ExecuteScalar() is not string json) return Task.FromResult<Rule?>(null);
        var rule = JsonSerializer.Deserialize<Rule>(json, JsonOptions)
                   ?? throw new InvalidOperationException($"rule {ruleId} deserialised to null");
        return Task.FromResult<Rule?>(rule);
    }

    public Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default)
    {
        var list = new List<RuleBinding>(_bindings.Count);
        foreach (var (key, b) in _bindings)
        {
            var sp = key.IndexOf(' ');
            if (sp <= 0 || !Enum.TryParse<HttpMethodKind>(key[..sp], out var method)) continue;
            list.Add(new RuleBinding(b.id, b.version, key[(sp + 1)..], method));
        }
        return Task.FromResult<IReadOnlyList<RuleBinding>>(list);
    }
}
