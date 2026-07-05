using System.Text.Json;
using RuleForge.Studio.Core.Connections;

namespace RuleForge.Studio.Core.Settings;

/// <summary>
/// Owns everything persisted under <c>%AppData%\RuleForge Studio</c>: settings.json, connections.json
/// and the DPAPI secret vault. Creates live connections (resolving API keys from the vault). Mirrors
/// DocumentForge Studio's <c>StudioWorkspace</c>.
/// </summary>
public sealed class StudioWorkspace
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _settingsPath;
    private readonly string _connectionsPath;

    public string RootDirectory { get; }
    public StudioSettings Settings { get; private set; }
    public List<ConnectionDescriptor> Connections { get; private set; }
    public SecretStore Secrets { get; }

    public StudioWorkspace(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RuleForge Studio");
        Directory.CreateDirectory(RootDirectory);

        _settingsPath = Path.Combine(RootDirectory, "settings.json");
        _connectionsPath = Path.Combine(RootDirectory, "connections.json");

        Settings = Load<StudioSettings>(_settingsPath) ?? new StudioSettings();
        Connections = Load<List<ConnectionDescriptor>>(_connectionsPath) ?? new();
        Secrets = new SecretStore(RootDirectory);
    }

    /// <summary>Add or update a connection; stores the API key in the secret vault when supplied.</summary>
    public void UpsertConnection(ConnectionDescriptor descriptor, string? apiKey = null)
    {
        if (apiKey is not null)
            descriptor.ApiKeySecretId = Secrets.Set(descriptor.ApiKeySecretId, apiKey);

        var idx = Connections.FindIndex(c => c.Id == descriptor.Id);
        if (idx >= 0) Connections[idx] = descriptor;
        else Connections.Add(descriptor);

        SaveConnections();
    }

    public void RemoveConnection(string id)
    {
        var existing = Connections.FirstOrDefault(c => c.Id == id);
        if (existing is null) return;
        if (existing.ApiKeySecretId is { } secretId) Secrets.Remove(secretId);
        Connections.Remove(existing);
        SaveConnections();
    }

    public void TouchLastConnected(string id)
    {
        var existing = Connections.FirstOrDefault(c => c.Id == id);
        if (existing is null) return;
        existing.LastConnectedUtc = DateTime.UtcNow;
        SaveConnections();
    }

    /// <summary>Create a live connection, resolving its API key from the vault.</summary>
    public IRuleForgeConnection CreateConnection(ConnectionDescriptor descriptor)
        => ConnectionFactory.Create(
            descriptor,
            descriptor.ApiKeySecretId is { } id ? Secrets.TryGet(id) : null);

    public void SaveSettings() => File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, Json));

    private void SaveConnections() => File.WriteAllText(_connectionsPath, JsonSerializer.Serialize(Connections, Json));

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            // Corrupt file — keep a copy and start fresh so the app still launches.
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* ignore */ }
            return null;
        }
    }
}
