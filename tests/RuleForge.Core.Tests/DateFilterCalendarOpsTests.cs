using System.Text.Json;
using RuleForge.Core.Evaluators;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Date filter calendar-predicate operators (#20):
/// day_of_week / day_of_month / month_of_year / is_weekend.
/// ISO-8601 day numbering (Mon=1..Sun=7).
/// </summary>
public class DateFilterCalendarOpsTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static StringFilterEvaluator.Context Ctx(JsonElement request) => new(request, Ctx: null);

    private static Verdict Eval(string sourcePath, DateFilterCompare compare, JsonElement request) =>
        DateFilterEvaluator.Evaluate(
            new DateFilterConfig(
                new DateFilterSource(SourceKind.Request, Path: sourcePath),
                compare,
                ArraySelector.First,
                OnMissing.Fail),
            Ctx(request)).Verdict;

    // ─── day_of_week ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-05-11", new[] { 1 }, true)]    // Mon=1 → in [1] → pass
    [InlineData("2026-05-13", new[] { 1, 3, 5 }, true)]   // Wed=3 → in list
    [InlineData("2026-05-09", new[] { 1, 2, 3, 4, 5 }, false)]   // Sat=6 → not in Mon-Fri
    [InlineData("2026-05-11", new int[0], false)]    // empty list → fail
    public void DayOfWeek_in_set(string date, int[] allowed, bool expectedPass)
    {
        var request = Json($$"""{"d":"{{date}}"}""");
        var v = Eval("$.d", new DateFilterCompare(
            DateFilterOperator.DayOfWeek, DateGranularity.Date,
            Values: allowed.ToList()), request);
        Assert.Equal(expectedPass ? Verdict.Pass : Verdict.Fail, v);
    }

    // ─── day_of_month ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-05-01", new[] { 1, 15 }, true)]
    [InlineData("2026-05-15", new[] { 1, 15 }, true)]
    [InlineData("2026-05-08", new[] { 1, 15 }, false)]
    public void DayOfMonth_in_set(string date, int[] allowed, bool expectedPass)
    {
        var request = Json($$"""{"d":"{{date}}"}""");
        var v = Eval("$.d", new DateFilterCompare(
            DateFilterOperator.DayOfMonth, DateGranularity.Date,
            Values: allowed.ToList()), request);
        Assert.Equal(expectedPass ? Verdict.Pass : Verdict.Fail, v);
    }

    // ─── month_of_year ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-03-15", new[] { 3, 6, 9, 12 }, true)]   // Q-end months
    [InlineData("2026-06-15", new[] { 3, 6, 9, 12 }, true)]
    [InlineData("2026-05-15", new[] { 3, 6, 9, 12 }, false)]
    public void MonthOfYear_in_set(string date, int[] allowed, bool expectedPass)
    {
        var request = Json($$"""{"d":"{{date}}"}""");
        var v = Eval("$.d", new DateFilterCompare(
            DateFilterOperator.MonthOfYear, DateGranularity.Date,
            Values: allowed.ToList()), request);
        Assert.Equal(expectedPass ? Verdict.Pass : Verdict.Fail, v);
    }

    // ─── is_weekend ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-05-09", null, true)]    // Sat, default (true)
    [InlineData("2026-05-10", null, true)]    // Sun, default (true)
    [InlineData("2026-05-11", null, false)]   // Mon
    [InlineData("2026-05-11", "false", true)] // weekday matched by "false"
    [InlineData("2026-05-09", "false", false)]
    [InlineData("2026-05-09", "true", true)]
    public void IsWeekend(string date, string? expected, bool expectedPass)
    {
        var request = Json($$"""{"d":"{{date}}"}""");
        var v = Eval("$.d", new DateFilterCompare(
            DateFilterOperator.IsWeekend, DateGranularity.Date,
            Value: expected), request);
        Assert.Equal(expectedPass ? Verdict.Pass : Verdict.Fail, v);
    }
}
