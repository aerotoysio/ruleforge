namespace RuleForge.Studio.Core;

/// <summary>
/// Assembly-wide constants for RuleForge Studio. Kept in the UI-free core so both the
/// WPF client and any headless tooling share one source of truth.
/// </summary>
public static class StudioInfo
{
    public const string AppName = "RuleForge Studio";
    public const string Version = "0.9.0";

    /// <summary>Default on-disk data directory for the client (rules workspace, caches).</summary>
    public const string DefaultDataDirectory = @"C:\data\ruleforge";
}
