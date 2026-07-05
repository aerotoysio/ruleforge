using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuleForge.Studio.Core.Settings;

/// <summary>
/// Per-Windows-user secret vault (secrets.json). Values are DPAPI-encrypted to the current user, so
/// the on-disk blob is useless on another machine/account. Mirrors DocumentForge Studio's store.
/// </summary>
public sealed class SecretStore
{
    private readonly string _path;
    private Dictionary<string, string> _protected;

    public SecretStore(string rootDirectory)
    {
        _path = Path.Combine(rootDirectory, "secrets.json");
        _protected = Load(_path);
    }

    /// <summary>Store (or replace) a secret; returns the id to reference it by.</summary>
    public string Set(string? id, string plaintext)
    {
        id ??= Guid.NewGuid().ToString("N");
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        _protected[id] = Convert.ToBase64String(blob);
        Save();
        return id;
    }

    public string? TryGet(string id)
    {
        if (!_protected.TryGetValue(id, out var b64)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (CryptographicException)
        {
            return null; // wrong user/profile
        }
    }

    public void Remove(string id)
    {
        if (_protected.Remove(id)) Save();
    }

    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Save()
        => File.WriteAllText(_path, JsonSerializer.Serialize(_protected, new JsonSerializerOptions { WriteIndented = true }));
}
