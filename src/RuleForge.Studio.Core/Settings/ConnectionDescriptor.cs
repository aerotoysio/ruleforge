using RuleForge.Studio.Core.Connections;

namespace RuleForge.Studio.Core.Settings;

/// <summary>
/// A saved connection (serialised to connections.json). Holds no secret — the API key lives in the
/// DPAPI-encrypted <see cref="SecretStore"/>, referenced by <see cref="ApiKeySecretId"/>.
/// </summary>
public sealed class ConnectionDescriptor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public RuleForgeConnectionKind Kind { get; set; }

    // Local workspace
    public string? WorkspaceDir { get; set; }

    // DocumentForge
    public string? Url { get; set; }
    public string? Database { get; set; }
    public string? ApiKeySecretId { get; set; }
    public string? Environment { get; set; }
    public string? CollectionPrefix { get; set; }

    public DateTime? LastConnectedUtc { get; set; }
}
