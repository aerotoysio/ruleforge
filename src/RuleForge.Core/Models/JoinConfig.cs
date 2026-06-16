namespace RuleForge.Core.Models;

/// <summary>
/// Enrich a "left" array with matching items from a "right" array, correlated
/// by key — e.g. attach each passenger's flights where flight.paxId ==
/// passenger.id. Left / Right are JSONPaths into the request / context (an empty
/// Right reads from the single upstream node, so it can be a computed array).
/// Mode "collect" attaches an array of all matches; "first" attaches a single
/// match (or null). The output is the left array, each item carrying the new
/// <c>As</c> field.
/// </summary>
public sealed record JoinConfig(
    string LeftKey,
    string RightKey,
    string As,
    string? Left = null,
    string? Right = null,
    string? Mode = null,
    bool? OnlyMatched = null);
