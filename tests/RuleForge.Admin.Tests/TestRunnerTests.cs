using System.Text.Json.Nodes;
using RuleForge.Admin;

namespace RuleForge.Admin.Tests;

public class TestRunnerTests
{
    private static readonly DateOnly Eval = new(2026, 7, 9);

    private static JsonArray Statements(params JsonObject[] stmts) => new(stmts.Cast<JsonNode>().ToArray());

    private static JsonObject Statement(string id, string joiner, params JsonObject[] conditions) => new()
    {
        ["id"] = id,
        ["text"] = id,
        ["joiner"] = joiner,
        ["conditions"] = new JsonArray(conditions.Cast<JsonNode>().ToArray()),
    };

    private static JsonObject Condition(string fieldId, string type, string op, string value, string? fn = null, string? unit = null)
    {
        var c = new JsonObject
        {
            ["field"] = new JsonObject { ["id"] = fieldId, ["type"] = type, ["entity"] = fieldId.Split('.')[0] },
            ["op"] = op,
            ["value"] = value,
        };
        if (fn is not null) c["fn"] = fn;
        if (unit is not null) c["unit"] = unit;
        return c;
    }

    private static JsonObject Input(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ── age() ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2025-01-01", true)]   // 1 year old → under 2
    [InlineData("2024-07-10", true)]   // turns 2 tomorrow → still 1
    [InlineData("2024-07-09", false)]  // 2nd birthday today → exactly 2, not < 2
    [InlineData("2023-01-01", false)]  // 3 years old
    public void Age_in_years_respects_birthday_boundary(string dob, bool expectValid)
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Pax.DOB", "date", "<", "2", fn: "age", unit: "years")));
        var result = TestRunner.Run(stmts, Input("""{"Pax":{"DOB":"__DOB__"}}""".Replace("__DOB__", dob)), Eval);
        Assert.Equal(expectValid ? "VALID" : "REJECT", result.Verdict);
    }

    [Fact]
    public void Age_supports_months_and_days_units()
    {
        var months = Statements(Statement("s0", "AND",
            Condition("Pax.DOB", "date", ">=", "6", fn: "age", unit: "months")));
        Assert.Equal("VALID", TestRunner.Run(months, Input("""{"Pax":{"DOB":"2026-01-09"}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(months, Input("""{"Pax":{"DOB":"2026-01-10"}}"""), Eval).Verdict);

        var days = Statements(Statement("s0", "AND",
            Condition("Booking.Created", "date", "<=", "30", fn: "age", unit: "days")));
        Assert.Equal("VALID", TestRunner.Run(days, Input("""{"Booking":{"Created":"2026-06-09"}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(days, Input("""{"Booking":{"Created":"2026-05-01"}}"""), Eval).Verdict);
    }

    [Fact]
    public void Age_accepts_iso_timestamps()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Pax.DOB", "date", "<", "2", fn: "age", unit: "years")));
        var result = TestRunner.Run(stmts, Input("""{"Pax":{"DOB":"2025-06-01T14:30:00Z"}}"""), Eval);
        Assert.Equal("VALID", result.Verdict);
    }

    // ── missing / malformed input ────────────────────────────────────────────

    [Fact]
    public void Missing_field_fails_the_condition_with_actual_missing()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Pax.DOB", "date", "<", "2", fn: "age", unit: "years")));
        var result = TestRunner.Run(stmts, Input("""{"Pax":{"Type":"INF"}}"""), Eval);
        Assert.Equal("REJECT", result.Verdict);
        Assert.Equal("missing", result.Statements[0].Conditions[0].Actual);
    }

    [Fact]
    public void Unparseable_value_fails_gracefully_instead_of_throwing()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Pax.DOB", "date", "<", "2", fn: "age", unit: "years")));
        var result = TestRunner.Run(stmts, Input("""{"Pax":{"DOB":"not-a-date"}}"""), Eval);
        Assert.Equal("REJECT", result.Verdict);
        Assert.StartsWith("unreadable", result.Statements[0].Conditions[0].Actual);
    }

    // ── joiners ──────────────────────────────────────────────────────────────

    [Fact]
    public void And_requires_all_conditions()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Pax.Type", "enum", "=", "INF"),
            Condition("Pax.Weight", "number", "<", "10")));
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Pax":{"Type":"INF","Weight":8}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(stmts, Input("""{"Pax":{"Type":"INF","Weight":12}}"""), Eval).Verdict);
    }

    [Fact]
    public void Or_requires_any_condition()
    {
        var stmts = Statements(Statement("s0", "OR",
            Condition("Pax.Type", "enum", "=", "INF"),
            Condition("Pax.Type", "enum", "=", "CHD")));
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Pax":{"Type":"CHD"}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(stmts, Input("""{"Pax":{"Type":"ADT"}}"""), Eval).Verdict);
    }

    [Fact]
    public void Verdict_is_valid_only_when_every_statement_passes()
    {
        var stmts = Statements(
            Statement("s0", "AND", Condition("Pax.Type", "enum", "=", "INF")),
            Statement("s1", "AND", Condition("Pax.Weight", "number", "<", "10")));
        Assert.Equal("REJECT", TestRunner.Run(stmts, Input("""{"Pax":{"Type":"INF","Weight":12}}"""), Eval).Verdict);
    }

    [Fact]
    public void Statements_without_conditions_are_skipped()
    {
        var stmts = Statements(
            Statement("s0", "AND"),
            Statement("s1", "AND", Condition("Pax.Type", "enum", "=", "INF")));
        var result = TestRunner.Run(stmts, Input("""{"Pax":{"Type":"INF"}}"""), Eval);
        Assert.Equal("VALID", result.Verdict);
        Assert.Single(result.Statements); // s0 skipped, only s1 evaluated
    }

    // ── scalar comparisons ───────────────────────────────────────────────────

    [Fact]
    public void String_comparison_is_case_insensitive()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Booking.Channel", "string", "=", "WEB")));
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Booking":{"Channel":"web"}}"""), Eval).Verdict);
    }

    [Fact]
    public void Numbers_arrive_as_json_numbers_or_strings()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Bag.Weight", "number", "<=", "23", unit: "kg")));
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Bag":{"Weight":23}}"""), Eval).Verdict);
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Bag":{"Weight":"23"}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(stmts, Input("""{"Bag":{"Weight":23.5}}"""), Eval).Verdict);
    }

    [Fact]
    public void Plain_date_comparison_without_age_fn()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Segment.Departure", "date", ">", "2026-07-01")));
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Segment":{"Departure":"2026-08-15"}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(stmts, Input("""{"Segment":{"Departure":"2026-06-15"}}"""), Eval).Verdict);
    }

    [Fact]
    public void Boolean_fields_compare_and_invert()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Booking.Corporate", "boolean", "!=", "true")));
        Assert.Equal("VALID", TestRunner.Run(stmts, Input("""{"Booking":{"Corporate":false}}"""), Eval).Verdict);
        Assert.Equal("REJECT", TestRunner.Run(stmts, Input("""{"Booking":{"Corporate":true}}"""), Eval).Verdict);
    }

    [Fact]
    public void Nested_paths_resolve_through_dotted_field_ids()
    {
        var stmts = Statements(Statement("s0", "AND",
            Condition("Booking.Contact.Country", "string", "=", "GB")));
        Assert.Equal("VALID", TestRunner.Run(stmts,
            Input("""{"Booking":{"Contact":{"Country":"GB"}}}"""), Eval).Verdict);
    }
}
