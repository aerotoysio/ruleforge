using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// Keep the elements of an array that match a stack of conditions on one of
/// their fields (combined by <see cref="Match"/> — "all"/"any"). Unlike a gate
/// filter, the output is the matching SUBSET of the input array, so it composes
/// with Join (filter the right array before joining, or the result after).
/// <see cref="Source"/> is a JSONPath to the array (empty = the upstream node's
/// output). Conditions reuse the typed filter-compare shapes (string / number /
/// date), selected by <see cref="ValueType"/>, so a Filter-list condition
/// behaves exactly like the equivalent gate filter — just evaluated per element.
/// </summary>
public sealed record FilterListConfig(
    string Field,
    JsonElement Conditions,
    string ValueType,
    string? Source = null,
    string? Match = null);
