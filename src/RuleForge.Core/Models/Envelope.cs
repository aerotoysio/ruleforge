using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record Envelope(
    string RuleId,
    int RuleVersion,
    Decision Decision,
    string EvaluatedAt,
    JsonElement? Result,
    IReadOnlyList<TraceEntry>? Trace = null,
    long? DurationMs = null);

[JsonConverter(typeof(JsonStringEnumConverter<Decision>))]
public enum Decision
{
    [JsonStringEnumMemberName("apply")] Apply,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("error")] Error,
}

public sealed record TraceEntry(
    string NodeId,
    string StartedAt,
    long DurationMs,
    TraceOutcome Outcome,
    JsonElement? Input = null,
    JsonElement? Output = null,
    IReadOnlyDictionary<string, JsonElement>? CtxRead = null,
    IReadOnlyDictionary<string, JsonElement>? CtxWritten = null,
    string? SubRuleRunId = null,
    string? Error = null,
    // ─── #22 trace enrichment ─────────────────────────────────────────────
    // Populated on filter nodes so the explainability UI can render the
    // actual comparison ("source was 'GVA', list was [JFK, LHR, ...], op = in").
    // EvaluatedSource is masked to "***" when the source's JSONPath matches
    // a `sensitive: true` property in the rule's inputSchema (see #22 pairing).
    JsonElement? EvaluatedSource = null,
    JsonElement? EvaluatedLiteral = null,
    string? Operator = null,
    string? ArraySelectorReason = null);

[JsonConverter(typeof(JsonStringEnumConverter<TraceOutcome>))]
public enum TraceOutcome
{
    [JsonStringEnumMemberName("pass")] Pass,
    [JsonStringEnumMemberName("fail")] Fail,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("error")] Error,
}
