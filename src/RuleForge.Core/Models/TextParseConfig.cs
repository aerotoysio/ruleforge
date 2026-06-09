using System.Collections.Generic;
using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// Splits a string into named tokens via a friendly "{token}" pattern (no
/// regex — the literal text between placeholders are the delimiters), then
/// overlays those tokens onto a base object (typically a saved asset's values).
/// Example: input "SSR BIKE P1 F OVERSIZE", pattern "{ssr} {type} {paxRef} {class} {detail}",
/// Map { "type": "code" } → emits the base object with code = "BIKE".
/// </summary>
public sealed record TextParseConfig(
    string Source = "",
    string Pattern = "",
    IReadOnlyDictionary<string, string>? Map = null,
    JsonElement? Base = null);
