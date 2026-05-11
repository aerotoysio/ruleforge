using System.Text.Json;
using RuleForge.Core.Evaluators;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// NCalc helper functions added in #27 (also closes #26):
/// now / today / yearsBetween / dayOfWeek / Count / Length / Contains / etc.
/// All ISO-8601 (Mon=1..Sun=7). All UTC unless an explicit Clock is injected.
/// </summary>
public class CalcHelpersTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    private static readonly IDictionary<string, JsonElement> Ctx = new Dictionary<string, JsonElement>();

    // Fixed clock for deterministic tests of now() / today().
    private static readonly DateTimeOffset FixedNow =
        new(2026, 5, 9, 12, 30, 0, TimeSpan.Zero); // Saturday, 2026-05-09 12:30 UTC
    private static Func<DateTimeOffset> FixedClock() => () => FixedNow;

    [Fact]
    public void Now_returns_injected_clock_value()
    {
        var result = CalcEvaluator.Evaluate("now()", null, Ctx, Json("{}"), null, clock: FixedClock());
        var s = result!.Value.GetString();
        Assert.Contains("2026-05-09", s);
    }

    [Fact]
    public void Today_returns_clock_date_at_midnight()
    {
        var result = CalcEvaluator.Evaluate("today()", null, Ctx, Json("{}"), null, clock: FixedClock());
        var s = result!.Value.GetString();
        Assert.Contains("2026-05-09", s);
    }

    [Fact]
    public void YearsBetween_computes_whole_years()
    {
        // From 2000-06-15 to 2026-05-09 — birthday on June 15 hasn't happened yet in May, so 25 years.
        var result = CalcEvaluator.Evaluate(
            "yearsBetween(parseDate('2000-06-15'), parseDate('2026-05-09'))",
            null, Ctx, Json("{}"));
        Assert.Equal(25, result!.Value.GetInt32());
    }

    [Fact]
    public void YearsBetween_includes_birthday_if_passed()
    {
        // From 2000-06-15 to 2026-07-01 — birthday passed, full 26 years.
        var result = CalcEvaluator.Evaluate(
            "yearsBetween(parseDate('2000-06-15'), parseDate('2026-07-01'))",
            null, Ctx, Json("{}"));
        Assert.Equal(26, result!.Value.GetInt32());
    }

    [Fact]
    public void DayOfWeek_uses_ISO_numbering()
    {
        // 2026-05-09 is Saturday (ISO 6).
        var result = CalcEvaluator.Evaluate(
            "dayOfWeek(parseDate('2026-05-09'))", null, Ctx, Json("{}"));
        Assert.Equal(6, result!.Value.GetInt32());
    }

    [Fact]
    public void DayOfMonth_returns_day_number()
    {
        var result = CalcEvaluator.Evaluate(
            "dayOfMonth(parseDate('2026-05-09'))", null, Ctx, Json("{}"));
        Assert.Equal(9, result!.Value.GetInt32());
    }

    [Fact]
    public void MonthOfYear_returns_month_number()
    {
        var result = CalcEvaluator.Evaluate(
            "monthOfYear(parseDate('2026-05-09'))", null, Ctx, Json("{}"));
        Assert.Equal(5, result!.Value.GetInt32());
    }

    [Theory]
    [InlineData("2026-05-09", true)]   // Saturday
    [InlineData("2026-05-10", true)]   // Sunday
    [InlineData("2026-05-11", false)]  // Monday
    [InlineData("2026-05-15", false)]  // Friday
    public void IsWeekend_returns_correctly(string date, bool expected)
    {
        var result = CalcEvaluator.Evaluate(
            $"isWeekend(parseDate('{date}'))", null, Ctx, Json("{}"));
        Assert.Equal(expected, result!.Value.GetBoolean());
    }

    [Fact]
    public void AddYears_advances_by_n_years()
    {
        var result = CalcEvaluator.Evaluate(
            "yearsBetween(parseDate('2000-01-01'), addYears(parseDate('2000-01-01'), 18))",
            null, Ctx, Json("{}"));
        Assert.Equal(18, result!.Value.GetInt32());
    }

    [Fact]
    public void Count_returns_array_length()
    {
        var request = Json("""{"pax":[1,2,3,4,5]}""");
        var result = CalcEvaluator.Evaluate("Count(pax)", null, Ctx, request);
        Assert.Equal(5, result!.Value.GetInt32());
    }

    [Fact]
    public void Length_is_alias_for_Count()
    {
        var request = Json("""{"items":["a","b","c"]}""");
        var result = CalcEvaluator.Evaluate("Length(items)", null, Ctx, request);
        Assert.Equal(3, result!.Value.GetInt32());
    }

    [Fact]
    public void Count_on_empty_array_is_zero()
    {
        var result = CalcEvaluator.Evaluate("Count(empty)", null, Ctx, Json("""{"empty":[]}"""));
        Assert.Equal(0, result!.Value.GetInt32());
    }

    [Fact]
    public void Count_enables_non_empty_assert_pattern()
    {
        // Closes #26: "Count(quotes) > 0" is the canonical non-empty check.
        var result = CalcEvaluator.Evaluate(
            "Count(quotes) > 0", null, Ctx, Json("""{"quotes":[{"price":100}]}"""));
        Assert.True(result!.Value.GetBoolean());

        result = CalcEvaluator.Evaluate(
            "Count(quotes) > 0", null, Ctx, Json("""{"quotes":[]}"""));
        Assert.False(result!.Value.GetBoolean());
    }

    [Fact]
    public void Contains_array_membership()
    {
        var request = Json("""{"tags":["GB","FR","DE"]}""");
        var hit = CalcEvaluator.Evaluate("Contains(tags, 'FR')", null, Ctx, request);
        Assert.True(hit!.Value.GetBoolean());

        var miss = CalcEvaluator.Evaluate("Contains(tags, 'IT')", null, Ctx, request);
        Assert.False(miss!.Value.GetBoolean());
    }

    [Fact]
    public void Helpers_compose_real_age_check()
    {
        // age ≥ 18 check. NCalc identifiers can't contain dots, so the calc
        // node expects flat top-level fields (or an upstream-resolved object
        // with the field accessible as a bare name). For nested data, the
        // editor / authoring layer typically resolves the value upstream
        // via a mutator-set-from-path before the calc fires.
        var request = Json("""{"dateOfBirth":"2000-06-15"}""");
        var result = CalcEvaluator.Evaluate(
            "yearsBetween(parseDate(dateOfBirth), now()) >= 18",
            null, Ctx, request, frames: null, clock: FixedClock());
        Assert.True(result!.Value.GetBoolean());
    }
}
