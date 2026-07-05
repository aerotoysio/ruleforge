using RuleForge.Studio.Core.Settings;

namespace RuleForge.Studio.Core.Connections;

/// <summary>Builds a live <see cref="IRuleForgeConnection"/> from a saved descriptor.</summary>
public static class ConnectionFactory
{
    public static IRuleForgeConnection Create(ConnectionDescriptor descriptor, string? apiKey) => descriptor.Kind switch
    {
        RuleForgeConnectionKind.LocalWorkspace => new LocalWorkspaceConnection(
            Path.Combine(descriptor.WorkspaceDir!, "rules"),
            Path.Combine(descriptor.WorkspaceDir!, "refs"),
            descriptor.Name),

        RuleForgeConnectionKind.DocumentForge => new DocumentForgeConnection(descriptor, apiKey),

        _ => throw new ArgumentOutOfRangeException(nameof(descriptor), $"Unknown connection kind {descriptor.Kind}"),
    };
}
