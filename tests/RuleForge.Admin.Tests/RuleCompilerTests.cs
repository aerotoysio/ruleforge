using System.Text.Json;
using System.Text.Json.Nodes;
using RuleForge.Admin;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;

namespace RuleForge.Admin.Tests;

/// <summary>
/// The compiler's contract test: compiled rules must load into the Core model
/// and produce the same verdicts through the REAL RuleForge engine that the
/// Admin TestRunner produces directly.
/// </summary>
public class RuleCompilerTests
{
    // Pinned engine clock — all age() maths evaluate relative to this instant.
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject InfantPolicy() => new()
    {
        ["title"] = "Infant Passenger Policy",
        ["version"] = 4,
        ["statements"] = new JsonArray(
            Statement("s0", "AND",
                Condition("Pax.Type", "enum", "=", "INF"),
                Condition("Pax.DOB", "date", "<", "2", fn: "age", unit: "years")),
            Statement("s1", "AND",
                Condition("Pax.Type", "enum", "=", "INF"),
                Condition("Pax.AccompaniedBy", "enum", "=", "ADT")),
            Statement("s2", "AND",
                Condition("Booking.InfantCount", "number", "<=", "2"),
                Condition("Booking.AdultCount", "number", ">=", "1"))),
    };

    private static JsonObject Statement(string id, string joiner, params JsonObject[] conditions) => new()
    {
        ["id"] = id, ["text"] = id, ["joiner"] = joiner,
        ["conditions"] = new JsonArray(conditions.Cast<JsonNode>().ToArray()),
    };

    private static JsonObject Condition(string fieldId, string type, string op, string value, string? fn = null, string? unit = null)
    {
        var c = new JsonObject
        {
            ["field"] = new JsonObject { ["id"] = fieldId, ["type"] = type },
            ["op"] = op, ["value"] = value,
        };
        if (fn is not null) c["fn"] = fn;
        if (unit is not null) c["unit"] = unit;
        return c;
    }

    private static string RunCompiled(JsonObject policy, string inputJson)
    {
        var compiled = RuleCompiler.Compile(policy, "d-test", "/v1/policies/test");
        var rule = JsonSerializer.Deserialize<Rule>(compiled.Rule.ToJsonString(), AeroJson.Options)!;

        using var doc = JsonDocument.Parse(inputJson);
        var runner = new RuleRunner();
        var envelope = runner.RunAsync(rule, doc.RootElement.Clone(),
            new RuleRunner.Options(Clock: () => Now)).GetAwaiter().GetResult();

        Assert.Equal(Decision.Apply, envelope.Decision);
        return envelope.Result!.Value.GetProperty("verdict").GetString()!;
    }

    // ── the three seeded demo scenarios, through the real engine ────────────

    [Fact]
    public void Infant_under_two_with_adult_is_valid()
    {
        var verdict = RunCompiled(InfantPolicy(), """
            {"Pax":{"Type":"INF","DOB":"2025-01-15","AccompaniedBy":"ADT"},
             "Booking":{"InfantCount":1,"AdultCount":2}}
            """);
        Assert.Equal("VALID", verdict);
    }

    [Fact]
    public void Three_year_old_typed_inf_is_rejected()
    {
        var verdict = RunCompiled(InfantPolicy(), """
            {"Pax":{"Type":"INF","DOB":"2023-03-01","AccompaniedBy":"ADT"},
             "Booking":{"InfantCount":1,"AdultCount":1}}
            """);
        Assert.Equal("REJECT", verdict);
    }

    [Fact]
    public void Missing_dob_is_rejected()
    {
        var verdict = RunCompiled(InfantPolicy(), """
            {"Pax":{"Type":"INF","AccompaniedBy":"ADT"},
             "Booking":{"InfantCount":1,"AdultCount":1}}
            """);
        Assert.Equal("REJECT", verdict);
    }

    // ── operator/inversion mappings ──────────────────────────────────────────

    [Fact]
    public void Age_gte_inverts_through_a_not_node()
    {
        var policy = new JsonObject
        {
            ["title"] = "Adults only", ["version"] = 1,
            ["statements"] = new JsonArray(
                Statement("s0", "AND",
                    Condition("Pax.DOB", "date", ">=", "18", fn: "age", unit: "years"))),
        };
        Assert.Equal("VALID", RunCompiled(policy, """{"Pax":{"DOB":"2000-01-01"}}"""));
        Assert.Equal("REJECT", RunCompiled(policy, """{"Pax":{"DOB":"2020-01-01"}}"""));
    }

    [Fact]
    public void Or_joiner_passes_when_any_condition_holds()
    {
        var policy = new JsonObject
        {
            ["title"] = "Channel gate", ["version"] = 1,
            ["statements"] = new JsonArray(
                Statement("s0", "OR",
                    Condition("Booking.Channel", "enum", "=", "WEB"),
                    Condition("Booking.Channel", "enum", "=", "MOBILE"))),
        };
        Assert.Equal("VALID", RunCompiled(policy, """{"Booking":{"Channel":"MOBILE"}}"""));
        Assert.Equal("REJECT", RunCompiled(policy, """{"Booking":{"Channel":"NDC"}}"""));
    }

    [Fact]
    public void Date_lte_compiles_as_not_after()
    {
        var policy = new JsonObject
        {
            ["title"] = "Booking window", ["version"] = 1,
            ["statements"] = new JsonArray(
                Statement("s0", "AND",
                    Condition("Segment.Departure", "date", "<=", "2026-08-01"))),
        };
        Assert.Equal("VALID", RunCompiled(policy, """{"Segment":{"Departure":"2026-08-01"}}"""));
        Assert.Equal("VALID", RunCompiled(policy, """{"Segment":{"Departure":"2026-07-20"}}"""));
        Assert.Equal("REJECT", RunCompiled(policy, """{"Segment":{"Departure":"2026-08-02"}}"""));
    }

    // ── compile-time failures ────────────────────────────────────────────────

    [Fact]
    public void Empty_policy_fails_compilation()
    {
        var policy = new JsonObject
        {
            ["title"] = "Empty", ["version"] = 1, ["statements"] = new JsonArray(),
        };
        var ex = Assert.Throws<RuleCompiler.CompileException>(
            () => RuleCompiler.Compile(policy, "d-empty", "/v1/policies/empty"));
        Assert.Contains("no compilable rule statements", ex.Errors[0]);
    }

    [Fact]
    public void Unsupported_string_operator_fails_compilation()
    {
        var policy = new JsonObject
        {
            ["title"] = "Bad op", ["version"] = 1,
            ["statements"] = new JsonArray(
                Statement("s0", "AND", Condition("Pax.Type", "enum", "<", "INF"))),
        };
        var ex = Assert.Throws<RuleCompiler.CompileException>(
            () => RuleCompiler.Compile(policy, "d-bad", "/v1/policies/bad"));
        Assert.Contains("not supported for string/enum", ex.Errors[0]);
    }

    [Fact]
    public void Compiled_rule_deserializes_into_the_core_model()
    {
        var compiled = RuleCompiler.Compile(InfantPolicy(), "d-infant", "/v1/policies/infant");
        var rule = JsonSerializer.Deserialize<Rule>(compiled.Rule.ToJsonString(), AeroJson.Options);
        Assert.NotNull(rule);
        Assert.Equal("rule-d-infant", rule!.Id);
        Assert.Equal(4, rule.CurrentVersion);
        Assert.Single(rule.Nodes, n => n.Data.Category == NodeCategory.Input);
        Assert.Single(rule.Nodes, n => n.Data.Category == NodeCategory.Output);
        Assert.Equal(2, rule.Nodes.Count(n => n.Data.Category == NodeCategory.Constant));
    }
}
