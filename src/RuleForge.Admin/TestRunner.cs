using System.Globalization;
using System.Text.Json.Nodes;

namespace RuleForge.Admin;

/// <summary>
/// Evaluates a policy's parsed rule statements against a test-case input.
///
/// Statements arrive as the JSON the Studio parser produces:
///   { id, text, joiner: "AND"|"OR", conditions: [
///       { field: { id: "Pax.DOB", type: "date", ... }, fn?: "age",
///         op: "="|"!="|"&lt;"|"&lt;="|"&gt;"|"&gt;=", value, unit? } ] }
///
/// Verdict semantics (mirrors the Studio graph: each statement gates
/// VALID / else REJECT):
///   - a statement passes when its conditions combine true under its joiner
///   - statements with zero parsed conditions are skipped (authoring warnings,
///     excluded at publish time — same treatment here)
///   - the policy verdict is VALID iff every evaluated statement passes
///   - a referenced field missing from the input fails that condition
/// String/enum comparison is case-insensitive.
/// </summary>
public static class TestRunner
{
    public sealed record ConditionResult(string Expr, bool Pass, string Actual);
    public sealed record StatementResult(string Id, string Text, bool Pass, List<ConditionResult> Conditions);
    public sealed record RunResult(string Verdict, List<StatementResult> Statements);

    public static RunResult Run(JsonArray statements, JsonObject input, DateOnly evaluationDate)
    {
        var results = new List<StatementResult>();
        var verdict = true;

        foreach (var stmtNode in statements)
        {
            if (stmtNode is not JsonObject stmt) continue;
            var conditions = stmt["conditions"] as JsonArray;
            if (conditions is null || conditions.Count == 0) continue;

            var joiner = stmt["joiner"]?.GetValue<string>() ?? "AND";
            var condResults = new List<ConditionResult>();

            foreach (var condNode in conditions)
            {
                if (condNode is not JsonObject cond) continue;
                condResults.Add(EvaluateCondition(cond, input, evaluationDate));
            }

            var pass = joiner.Equals("OR", StringComparison.OrdinalIgnoreCase)
                ? condResults.Any(c => c.Pass)
                : condResults.All(c => c.Pass);

            verdict &= pass;
            results.Add(new StatementResult(
                stmt["id"]?.GetValue<string>() ?? $"s{results.Count}",
                stmt["text"]?.GetValue<string>() ?? "",
                pass,
                condResults));
        }

        return new RunResult(verdict ? "VALID" : "REJECT", results);
    }

    private static ConditionResult EvaluateCondition(JsonObject cond, JsonObject input, DateOnly evaluationDate)
    {
        var field = cond["field"] as JsonObject;
        var fieldId = field?["id"]?.GetValue<string>() ?? "?";
        var fieldType = field?["type"]?.GetValue<string>() ?? "string";
        var fn = cond["fn"]?.GetValue<string>();
        var op = cond["op"]?.GetValue<string>() ?? "=";
        var value = cond["value"]?.GetValue<string>() ?? "";
        var unit = cond["unit"]?.GetValue<string>();

        var lhs = fn is null ? fieldId : $"{fn}({fieldId})";
        var expr = $"{lhs} {op} {value}{(unit is null ? "" : " " + unit)}";

        var node = Resolve(input, fieldId);
        if (node is null)
            return new ConditionResult(expr, Pass: false, Actual: "missing");

        try
        {
            return fn switch
            {
                "age" => EvaluateAge(expr, node, op, value, unit, evaluationDate),
                _ => fieldType switch
                {
                    "number" => EvaluateNumber(expr, node, op, value),
                    "date" => EvaluateDate(expr, node, op, value),
                    "boolean" => EvaluateBoolean(expr, node, op, value),
                    _ => EvaluateString(expr, node, op, value),
                },
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return new ConditionResult(expr, Pass: false, Actual: $"unreadable: {node.ToJsonString()}");
        }
    }

    /// <summary>Walk a dotted field id ("Pax.DOB") through the input object.</summary>
    private static JsonNode? Resolve(JsonObject input, string fieldId)
    {
        JsonNode? current = input;
        foreach (var segment in fieldId.Split('.'))
        {
            if (current is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(segment, out current) || current is null) return null;
        }
        return current;
    }

    private static ConditionResult EvaluateAge(
        string expr, JsonNode node, string op, string value, string? unit, DateOnly evaluationDate)
    {
        var dob = ParseDate(node);
        double age;
        string unitLabel;
        var normalizedUnit = (unit ?? "years").ToLowerInvariant();

        if (normalizedUnit.StartsWith("day"))
        {
            age = evaluationDate.DayNumber - dob.DayNumber;
            unitLabel = "days";
        }
        else if (normalizedUnit.StartsWith("month"))
        {
            age = (evaluationDate.Year - dob.Year) * 12 + evaluationDate.Month - dob.Month
                  - (evaluationDate.Day < dob.Day ? 1 : 0);
            unitLabel = "months";
        }
        else
        {
            // Compare month/day, not DayOfYear — leap years shift DayOfYear
            // by one after February and misplace the birthday boundary.
            var beforeBirthday = evaluationDate.Month < dob.Month
                                 || (evaluationDate.Month == dob.Month && evaluationDate.Day < dob.Day);
            age = evaluationDate.Year - dob.Year - (beforeBirthday ? 1 : 0);
            unitLabel = "years";
        }

        var target = double.Parse(value, CultureInfo.InvariantCulture);
        return new ConditionResult(expr, Compare(age, target, op), $"{age} {unitLabel} (DOB {dob:yyyy-MM-dd})");
    }

    private static ConditionResult EvaluateNumber(string expr, JsonNode node, string op, string value)
    {
        var actual = node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture)
            : node.GetValue<double>();
        var target = double.Parse(value, CultureInfo.InvariantCulture);
        return new ConditionResult(expr, Compare(actual, target, op), actual.ToString(CultureInfo.InvariantCulture));
    }

    private static ConditionResult EvaluateDate(string expr, JsonNode node, string op, string value)
    {
        var actual = ParseDate(node);
        var target = DateOnly.Parse(value, CultureInfo.InvariantCulture);
        return new ConditionResult(expr, Compare(actual.DayNumber, target.DayNumber, op), actual.ToString("yyyy-MM-dd"));
    }

    private static ConditionResult EvaluateBoolean(string expr, JsonNode node, string op, string value)
    {
        var actual = node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? bool.Parse(node.GetValue<string>())
            : node.GetValue<bool>();
        var target = bool.Parse(value);
        var equal = actual == target;
        var pass = op == "!=" ? !equal : equal;
        return new ConditionResult(expr, pass, actual ? "true" : "false");
    }

    private static ConditionResult EvaluateString(string expr, JsonNode node, string op, string value)
    {
        var actual = node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToJsonString().Trim('"');
        var cmp = string.Compare(actual, value, StringComparison.OrdinalIgnoreCase);
        var pass = op switch
        {
            "=" => cmp == 0,
            "!=" => cmp != 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            _ => false,
        };
        return new ConditionResult(expr, pass, actual);
    }

    private static bool Compare(double actual, double target, string op) => op switch
    {
        "=" => actual == target,
        "!=" => actual != target,
        "<" => actual < target,
        "<=" => actual <= target,
        ">" => actual > target,
        ">=" => actual >= target,
        _ => false,
    };

    private static DateOnly ParseDate(JsonNode node)
    {
        var s = node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToJsonString().Trim('"');
        // Accept full ISO timestamps as well as plain dates.
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? DateOnly.FromDateTime(dto.UtcDateTime)
            : DateOnly.Parse(s, CultureInfo.InvariantCulture);
    }
}
