using System.Globalization;
using System.Text.Json;
using NCalc;
using RuleForge.Core.Models;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Evaluates a calc node's expression and produces a JsonElement. Variables
/// are resolved from a stacked namespace: upstream fields shadow ctx keys
/// shadow request fields. Numeric results round-trip back to JSON numbers;
/// booleans to JSON booleans; strings to JSON strings.
/// </summary>
public static class CalcEvaluator
{
    /// <summary>
    /// Default per-call deadline for calc expressions. NCalcSync has no native
    /// cancellation, so we race <c>Evaluate()</c> against this on a Task. The
    /// deadline guards against runaway expressions like <c>pow(pow(pow(...)))</c>
    /// that an authoring mistake could plant on the hot path.
    /// <para>
    /// 5 seconds is far longer than any sane expression should take (sub-ms
    /// is the warm-state target) but provides headroom against thread-pool
    /// scheduling delay under heavy parallel load.
    /// </para>
    /// </summary>
    public const int DefaultTimeoutMs = 5000;

    public static JsonElement? Evaluate(
        string expression,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request) =>
        Evaluate(expression, upstream, ctx, request, frames: null);

    public static JsonElement? Evaluate(
        string expression,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request,
        IReadOnlyList<IterationFrame>? frames,
        int timeoutMs = DefaultTimeoutMs,
        Func<DateTimeOffset>? clock = null)
    {
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be > 0");

        // Default options: case-sensitive operators / functions. We do our own
        // case-insensitive variable resolution against the JSON inputs below.
        var expr = new Expression(expression);

        expr.EvaluateParameter += (name, args) =>
        {
            if (TryResolveVariable(name, upstream, ctx, request, frames, out var resolved))
                args.Result = resolved;
        };

        // Custom NCalc helpers (#27). Closes #26 (Count covers non-empty check).
        // ISO-8601 day-of-week (Mon=1 .. Sun=7). All date helpers use UTC unless
        // the caller injects a different clock via `clock`.
        expr.EvaluateFunction += (name, args) =>
        {
            DateTimeOffset Now() => clock?.Invoke() ?? DateTimeOffset.UtcNow;
            switch (name.ToLowerInvariant())
            {
                case "now":          args.Result = Now().UtcDateTime; break;
                case "today":        args.Result = Now().UtcDateTime.Date; break;
                case "parsedate":    args.Result = ToDateTime(args.Parameters[0].Evaluate()); break;
                case "yearsbetween": args.Result = YearsBetween(ToDateTime(args.Parameters[0].Evaluate()),
                                                                ToDateTime(args.Parameters[1].Evaluate())); break;
                case "monthsbetween": args.Result = MonthsBetween(ToDateTime(args.Parameters[0].Evaluate()),
                                                                  ToDateTime(args.Parameters[1].Evaluate())); break;
                case "daysbetween":  args.Result = (int)Math.Abs((ToDateTime(args.Parameters[0].Evaluate())
                                                                  - ToDateTime(args.Parameters[1].Evaluate())).TotalDays); break;
                case "dayofweek":    args.Result = IsoDayOfWeek(ToDateTime(args.Parameters[0].Evaluate())); break;
                case "dayofmonth":   args.Result = ToDateTime(args.Parameters[0].Evaluate()).Day; break;
                case "monthofyear":  args.Result = ToDateTime(args.Parameters[0].Evaluate()).Month; break;
                case "dayofyear":    args.Result = ToDateTime(args.Parameters[0].Evaluate()).DayOfYear; break;
                case "isweekend":    var dow = IsoDayOfWeek(ToDateTime(args.Parameters[0].Evaluate()));
                                     args.Result = dow == 6 || dow == 7; break;
                case "adddays":      args.Result = ToDateTime(args.Parameters[0].Evaluate())
                                                    .AddDays(Convert.ToDouble(args.Parameters[1].Evaluate())); break;
                case "addmonths":    args.Result = ToDateTime(args.Parameters[0].Evaluate())
                                                    .AddMonths(Convert.ToInt32(args.Parameters[1].Evaluate())); break;
                case "addyears":     args.Result = ToDateTime(args.Parameters[0].Evaluate())
                                                    .AddYears(Convert.ToInt32(args.Parameters[1].Evaluate())); break;
                case "formatdate":
                    var d = ToDateTime(args.Parameters[0].Evaluate());
                    var fmt = args.Parameters.Length > 1 ? args.Parameters[1].Evaluate()?.ToString() : null;
                    args.Result = string.IsNullOrEmpty(fmt) ? d.ToString("O", CultureInfo.InvariantCulture)
                                                            : d.ToString(fmt, CultureInfo.InvariantCulture);
                    break;
                case "count":
                case "length":       args.Result = ToLength(args.Parameters[0].Evaluate()); break;
                case "contains":     args.Result = ContainsHelper(args.Parameters[0].Evaluate(),
                                                                  args.Parameters[1].Evaluate()); break;
            }
        };

        // Race expr.Evaluate() against the deadline. NCalcSync evaluation is
        // CPU-bound and uncancellable; if the wait times out we fail fast. The
        // background task may continue spinning until NCalc finishes — that's
        // a known leak pending NCalc cancellation support, but the request
        // returns promptly.
        object? result;
        try
        {
            var task = Task.Run(() => expr.Evaluate());
            if (!task.Wait(timeoutMs))
            {
                throw new InvalidOperationException(
                    $"calc expression '{expression}' timed out after {timeoutMs}ms " +
                    "(likely a runaway / deeply-nested expression)");
            }
            result = task.GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"calc expression '{expression}' failed to evaluate: {e.Message}", e);
        }

        return CoerceToJson(result);
    }

    private static bool TryResolveVariable(
        string name,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request,
        IReadOnlyList<IterationFrame>? frames,
        out object? value)
    {
        // Upstream fields take priority.
        if (upstream is { ValueKind: JsonValueKind.Object } u &&
            TryReadProperty(u, name, out value))
            return true;

        // Iteration frames (innermost first): bare name → frame.Item if it's a
        // primitive; <name>Index / <name>Count → integer.
        if (frames is { Count: > 0 })
        {
            for (var i = frames.Count - 1; i >= 0; i--)
            {
                var f = frames[i];
                if (string.Equals(name, f.Name, StringComparison.Ordinal))
                {
                    if (TryUnwrap(f.Item, out value)) return true;
                    break;
                }
                if (string.Equals(name, f.Name + "Index", StringComparison.Ordinal))
                {
                    value = (long)f.Index; return true;
                }
                if (string.Equals(name, f.Name + "Count", StringComparison.Ordinal))
                {
                    value = (long)f.Count; return true;
                }
            }
        }

        // Then ctx.
        if (ctx.TryGetValue(name, out var ctxEl) && TryUnwrap(ctxEl, out value))
            return true;

        // Then request top-level.
        if (request.ValueKind == JsonValueKind.Object &&
            TryReadProperty(request, name, out value))
            return true;

        value = null;
        return false;
    }

    private static bool TryReadProperty(JsonElement obj, string name, out object? value)
    {
        // Case-insensitive scan to match NCalc's IgnoreCase.
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.NameEquals(name) ||
                string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return TryUnwrap(prop.Value, out value);
            }
        }
        value = null;
        return false;
    }

    private static bool TryUnwrap(JsonElement el, out object? value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var i)) { value = i; return true; }
                if (el.TryGetDouble(out var d)) { value = d; return true; }
                break;
            case JsonValueKind.String:
                value = el.GetString();
                return true;
            case JsonValueKind.True:  value = true;  return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.Null:  value = null;  return true;
            case JsonValueKind.Array:
            case JsonValueKind.Object:
                // Pass through the JsonElement itself so helpers like Count()
                // and Contains() can inspect arrays/objects directly (#27).
                value = el;
                return true;
        }
        value = null;
        return false;
    }

    // ─── helpers (#27) ──────────────────────────────────────────────────────

    private static DateTime ToDateTime(object? v)
    {
        return v switch
        {
            DateTime dt        => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            JsonElement el when el.ValueKind == JsonValueKind.String => ToDateTime(el.GetString()),
            null => throw new InvalidOperationException("date helper received null"),
            _    => throw new InvalidOperationException(
                $"cannot convert '{v}' (type {v.GetType().Name}) to DateTime")
        };
    }

    /// <summary>ISO-8601 day-of-week (Mon=1 .. Sun=7).</summary>
    private static int IsoDayOfWeek(DateTime d) =>
        d.DayOfWeek == System.DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static int YearsBetween(DateTime a, DateTime b)
    {
        var (early, late) = a <= b ? (a, b) : (b, a);
        var years = late.Year - early.Year;
        if (late.Month < early.Month || (late.Month == early.Month && late.Day < early.Day))
            years--;
        return years;
    }

    private static int MonthsBetween(DateTime a, DateTime b)
    {
        var (early, late) = a <= b ? (a, b) : (b, a);
        var months = (late.Year - early.Year) * 12 + (late.Month - early.Month);
        if (late.Day < early.Day) months--;
        return months;
    }

    private static long ToLength(object? v) => v switch
    {
        JsonElement el when el.ValueKind == JsonValueKind.Array  => el.GetArrayLength(),
        JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString()?.Length ?? 0,
        JsonElement el when el.ValueKind == JsonValueKind.Object => el.EnumerateObject().Count(),
        string s                          => s.Length,
        System.Collections.ICollection c  => c.Count,
        null                              => 0,
        _ => throw new InvalidOperationException(
            $"cannot compute length of '{v}' (type {v.GetType().Name})")
    };

    private static bool ContainsHelper(object? haystack, object? needle)
    {
        if (haystack is null) return false;
        if (haystack is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                var needleStr = needle?.ToString() ?? "";
                foreach (var item in el.EnumerateArray())
                {
                    var itemStr = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                    if (string.Equals(itemStr, needleStr, StringComparison.Ordinal)) return true;
                }
                return false;
            }
            if (el.ValueKind == JsonValueKind.String && needle is not null)
                return el.GetString()?.Contains(needle.ToString() ?? "", StringComparison.Ordinal) ?? false;
        }
        if (haystack is string s) return s.Contains(needle?.ToString() ?? "", StringComparison.Ordinal);
        return false;
    }

    private static JsonElement? CoerceToJson(object? result) => result switch
    {
        null    => null,
        bool b  => JsonDocument.Parse(b ? "true" : "false").RootElement,
        int  i  => JsonDocument.Parse(i.ToString(CultureInfo.InvariantCulture)).RootElement,
        long l  => JsonDocument.Parse(l.ToString(CultureInfo.InvariantCulture)).RootElement,
        double d when double.IsFinite(d) => JsonDocument.Parse(FormatDouble(d)).RootElement,
        decimal dec => JsonDocument.Parse(dec.ToString(CultureInfo.InvariantCulture)).RootElement,
        DateTime dt => JsonDocument.Parse(JsonSerializer.Serialize(
            dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))).RootElement,
        string s => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement,
        JsonElement je => je,
        _        => JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement,
    };

    private static string FormatDouble(double d)
    {
        // Prefer integer form when the value is exact, to keep JSON tidy.
        if (d == Math.Truncate(d) && d >= long.MinValue && d <= long.MaxValue)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
