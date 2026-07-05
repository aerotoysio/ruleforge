namespace RuleForge.Studio.Core.Settings;

/// <summary>User-level Studio settings (settings.json).</summary>
public sealed class StudioSettings
{
    public string DefaultDataDirectory { get; set; } = StudioInfo.DefaultDataDirectory;
    public bool ReconnectOnStartup { get; set; } = true;
}
