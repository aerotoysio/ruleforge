using System.Globalization;
using System.Text.Json.Nodes;

namespace RuleForge.Admin;

/// <summary>
/// Compiles a policy's parsed rule statements into a RuleForge rule graph —
/// the JSON shape RuleForge.Core loads and evaluates.
///
/// Topology (verdict rule):
///
///   input ──default──▶ filter(cond) [──default──▶ not]   … per condition
///            statement joiner: single terminal, or logic and/or over terminals
///   statement terminals ──default──▶ final AND ──pass──▶ const {verdict:VALID}
///                                              ──fail──▶ const {verdict:REJECT}
///   both constants ──default──▶ output
///
/// Condition→logic edges are `default` on purpose: branches gate traversal,
/// and the verdict node must fire on failures too so REJECT can route.
/// Logic nodes read every upstream verdict regardless of branch.
///
/// age(field) op N unit maps onto date filters relative to evaluation time:
///   age &lt; N  → within_last(N)          age ≥ N → NOT within_last(N)
/// (years become months×12 — the engine's DateUnit has no years). The exact-
/// birthday instant sits on the within_last boundary, so `&lt;=`/`&gt;` compile
/// with a boundary warning.
/// </summary>
public static class RuleCompiler
{
    public sealed record CompileResult(JsonObject Rule, List<string> Warnings);

    public sealed class CompileException(List<string> errors)
        : Exception("policy failed to compile: " + string.Join("; ", errors))
    {
        public List<string> Errors { get; } = errors;
    }

    private const int ColW = 240;
    private const int RowH = 160;

    public static CompileResult Compile(
        JsonObject policy, string policyId, string endpoint, string updatedBy = "policy-studio")
    {
        var statements = policy["statements"] as JsonArray ?? new JsonArray();
        var version = policy["version"]?.GetValue<int>() ?? 1;
        var title = policy["title"]?.GetValue<string>() ?? policyId;

        var errors = new List<string>();
        var warnings = new List<string>();
        var nodes = new JsonArray();
        var edges = new JsonArray();
        var edgeSeq = 0;

        string AddEdge(string source, string target, string branch)
        {
            var id = $"e{++edgeSeq}";
            edges.Add(new JsonObject
            {
                ["id"] = id, ["source"] = source, ["target"] = target, ["branch"] = branch,
            });
            return id;
        }

        nodes.Add(Node("n-input", "input", "Request", 40, 40, templateId: null, config: null));

        var statementTerminals = new List<string>();
        var row = 0;

        foreach (var stmtNode in statements)
        {
            if (stmtNode is not JsonObject stmt) continue;
            var conditions = stmt["conditions"] as JsonArray;
            if (conditions is null || conditions.Count == 0) continue; // authoring warnings — excluded, like publish

            var stmtId = stmt["id"]?.GetValue<string>() ?? $"s{row}";
            var joiner = (stmt["joiner"]?.GetValue<string>() ?? "AND").ToUpperInvariant();
            var y = 40 + ++row * RowH;
            var col = 0;
            var conditionTerminals = new List<string>();

            for (var ci = 0; ci < conditions.Count; ci++)
            {
                if (conditions[ci] is not JsonObject cond) continue;
                var terminal = CompileCondition(
                    cond, $"{stmtId}-c{ci}", x: 280 + col * ColW, y,
                    nodes, AddEdge, errors, warnings);
                col += terminal.ColumnsUsed;
                if (terminal.NodeId is null) continue;
                AddEdge("n-input", terminal.EntryNodeId!, "default");
                conditionTerminals.Add(terminal.NodeId);
            }

            if (conditionTerminals.Count == 0) continue;

            if (conditionTerminals.Count == 1)
            {
                statementTerminals.Add(conditionTerminals[0]);
            }
            else
            {
                var op = joiner == "OR" ? "or" : "and";
                var logicId = $"{stmtId}-{op}";
                nodes.Add(Node(logicId, "logic", op.ToUpperInvariant(), 280 + col * ColW, y, $"sys-{op}", config: null));
                foreach (var t in conditionTerminals) AddEdge(t, logicId, "default");
                statementTerminals.Add(logicId);
            }
        }

        if (statementTerminals.Count == 0)
            errors.Add("policy has no compilable rule statements (every statement was empty or unparsed)");
        if (errors.Count > 0) throw new CompileException(errors);

        var maxY = 40 + (row + 1) * RowH;
        var verdictX = 280 + 4 * ColW;

        nodes.Add(Node("n-verdict", "logic", "AND", verdictX, 40, "sys-and", config: null));
        foreach (var t in statementTerminals) AddEdge(t, "n-verdict", "default");

        nodes.Add(Node("n-valid", "constant", "VALID", verdictX + ColW, 40, null,
            new JsonObject { ["value"] = VerdictValue("VALID", policyId, version) }));
        nodes.Add(Node("n-reject", "constant", "REJECT", verdictX + ColW, 40 + RowH, null,
            new JsonObject { ["value"] = VerdictValue("REJECT", policyId, version) }));
        AddEdge("n-verdict", "n-valid", "pass");
        AddEdge("n-verdict", "n-reject", "fail");

        nodes.Add(Node("n-output", "output", "Decision", verdictX + 2 * ColW, 40, null, config: null));
        AddEdge("n-valid", "n-output", "default");
        AddEdge("n-reject", "n-output", "default");

        var rule = new JsonObject
        {
            ["id"] = $"rule-{policyId}",
            ["name"] = title,
            ["description"] = $"Compiled from Policy Studio policy '{policyId}' v{version}.",
            ["tags"] = new JsonArray("policy-studio", policyId),
            ["category"] = "Policy",
            ["endpoint"] = endpoint,
            ["method"] = "POST",
            ["status"] = "published",
            ["currentVersion"] = version,
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
            ["outputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["verdict"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("VALID", "REJECT"),
                    },
                },
            },
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["updatedBy"] = updatedBy,
        };

        _ = maxY; // layout bookkeeping only
        return new CompileResult(rule, warnings);
    }

    private sealed record ConditionTerminal(string? NodeId, string? EntryNodeId, int ColumnsUsed);

    private static ConditionTerminal CompileCondition(
        JsonObject cond, string idBase, int x, int y,
        JsonArray nodes, Func<string, string, string, string> addEdge,
        List<string> errors, List<string> warnings)
    {
        var field = cond["field"] as JsonObject;
        var fieldId = field?["id"]?.GetValue<string>();
        var fieldType = field?["type"]?.GetValue<string>() ?? "string";
        var fn = cond["fn"]?.GetValue<string>();
        var op = cond["op"]?.GetValue<string>() ?? "=";
        var value = cond["value"]?.GetValue<string>() ?? "";
        var unit = cond["unit"]?.GetValue<string>();

        if (fieldId is null)
        {
            errors.Add($"{idBase}: condition has no bound schema field");
            return new ConditionTerminal(null, null, 0);
        }

        var path = "$." + fieldId;
        var label = (fn is null ? fieldId : $"{fn}({fieldId})") + $" {op} {value}";

        if (fn == "age")
            return CompileAge(idBase, label, path, op, value, unit, x, y, nodes, addEdge, errors, warnings);

        return fieldType switch
        {
            "number" => CompileNumber(idBase, label, path, op, value, x, y, nodes, errors),
            "date" => CompileDate(idBase, label, path, op, value, x, y, nodes, addEdge, errors),
            "boolean" => CompileBoolean(idBase, label, path, op, value, x, y, nodes, errors, warnings),
            _ => CompileString(idBase, label, path, op, value, x, y, nodes, errors),
        };
    }

    private static ConditionTerminal CompileString(
        string id, string label, string path, string op, string value,
        int x, int y, JsonArray nodes, List<string> errors)
    {
        var oper = op switch { "=" => "equals", "!=" => "not_equals", _ => null };
        if (oper is null)
        {
            errors.Add($"{id}: operator '{op}' is not supported for string/enum fields");
            return new ConditionTerminal(null, null, 0);
        }
        nodes.Add(FilterNode(id, label, "sys-filter-str", x, y, new JsonObject
        {
            ["source"] = new JsonObject { ["kind"] = "request", ["path"] = path },
            ["compare"] = new JsonObject
            {
                ["operator"] = oper, ["value"] = value,
                ["caseInsensitive"] = true, ["trim"] = true,
            },
            ["arraySelector"] = "first",
            ["onMissing"] = "fail",
        }));
        return new ConditionTerminal(id, id, 1);
    }

    private static ConditionTerminal CompileNumber(
        string id, string label, string path, string op, string value,
        int x, int y, JsonArray nodes, List<string> errors)
    {
        var oper = op switch
        {
            "=" => "equals", "!=" => "not_equals",
            "<" => "lt", "<=" => "lte", ">" => "gt", ">=" => "gte",
            _ => null,
        };
        if (oper is null || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            errors.Add($"{id}: cannot compile number condition '{label}'");
            return new ConditionTerminal(null, null, 0);
        }
        nodes.Add(FilterNode(id, label, "sys-filter-num", x, y, new JsonObject
        {
            ["source"] = new JsonObject { ["kind"] = "request", ["path"] = path },
            ["compare"] = new JsonObject { ["operator"] = oper, ["value"] = num },
            ["arraySelector"] = "first",
            ["onMissing"] = "fail",
        }));
        return new ConditionTerminal(id, id, 1);
    }

    private static ConditionTerminal CompileDate(
        string id, string label, string path, string op, string value,
        int x, int y, JsonArray nodes, Func<string, string, string, string> addEdge,
        List<string> errors)
    {
        // <= / >= have no direct DateFilterOperator — compile the strict
        // opposite and invert through a NOT logic node.
        var (oper, invert) = op switch
        {
            "<" => ("before", false),
            ">" => ("after", false),
            "=" => ("equals", false),
            "!=" => ("not_equals", false),
            "<=" => ("after", true),   // NOT after  ⇔ ≤
            ">=" => ("before", true),  // NOT before ⇔ ≥
            _ => ((string?)null, false),
        };
        if (oper is null)
        {
            errors.Add($"{id}: operator '{op}' is not supported for date fields");
            return new ConditionTerminal(null, null, 0);
        }
        nodes.Add(FilterNode(id, label, "sys-filter-date", x, y, new JsonObject
        {
            ["source"] = new JsonObject { ["kind"] = "request", ["path"] = path },
            ["compare"] = new JsonObject { ["operator"] = oper, ["value"] = value, ["granularity"] = "date" },
            ["arraySelector"] = "first",
            ["onMissing"] = "fail",
        }));
        if (!invert) return new ConditionTerminal(id, id, 1);
        return WrapNot(id, x + ColW, y, nodes, addEdge);
    }

    private static ConditionTerminal CompileBoolean(
        string id, string label, string path, string op, string value,
        int x, int y, JsonArray nodes, List<string> errors, List<string> warnings)
    {
        warnings.Add($"{id}: boolean field compiled as string comparison — booleans sent as JSON true/false may not match");
        return CompileString(id, label, path, op, value, x, y, nodes, errors);
    }

    private static ConditionTerminal CompileAge(
        string id, string label, string path, string op, string value, string? unit,
        int x, int y, JsonArray nodes, Func<string, string, string, string> addEdge,
        List<string> errors, List<string> warnings)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawAmount))
        {
            errors.Add($"{id}: age comparison needs a numeric value, got '{value}'");
            return new ConditionTerminal(null, null, 0);
        }

        var u = (unit ?? "years").Trim().ToLowerInvariant();
        string dateUnit;
        int amount;
        if (u.StartsWith("day")) { dateUnit = "days"; amount = (int)rawAmount; }
        else if (u.StartsWith("month")) { dateUnit = "months"; amount = (int)rawAmount; }
        else { dateUnit = "months"; amount = (int)(rawAmount * 12); } // years → months×12

        var invert = op switch
        {
            "<" => false,
            "<=" => false,
            ">=" => true,
            ">" => true,
            _ => (bool?)null,
        } ?? throw new CompileException([$"{id}: operator '{op}' is not supported for age() comparisons"]);

        if (op is "<=" or ">")
            warnings.Add($"{id}: '{op}' on age() compiles to the within_last boundary — the exact-birthday instant lands on the {(op == "<=" ? "excluded" : "included")} side");

        nodes.Add(FilterNode(id, label, "sys-filter-date", x, y, new JsonObject
        {
            ["source"] = new JsonObject { ["kind"] = "request", ["path"] = path },
            ["compare"] = new JsonObject
            {
                ["operator"] = "within_last",
                ["amount"] = amount,
                ["unit"] = dateUnit,
                ["granularity"] = "date",
            },
            ["arraySelector"] = "first",
            ["onMissing"] = "fail",
        }));

        if (!invert) return new ConditionTerminal(id, id, 1);
        return WrapNot(id, x + ColW, y, nodes, addEdge);
    }

    private static ConditionTerminal WrapNot(
        string filterId, int x, int y, JsonArray nodes,
        Func<string, string, string, string> addEdge)
    {
        var notId = $"{filterId}-not";
        nodes.Add(Node(notId, "logic", "NOT", x, y, "sys-not", config: null));
        addEdge(filterId, notId, "default");
        return new ConditionTerminal(notId, filterId, 2);
    }

    private static JsonObject FilterNode(string id, string label, string templateId, int x, int y, JsonObject config)
        => Node(id, "filter", label, x, y, templateId, config);

    private static JsonObject Node(
        string id, string category, string label, int x, int y, string? templateId, JsonObject? config)
    {
        var data = new JsonObject { ["label"] = label, ["category"] = category };
        if (templateId is not null) data["templateId"] = templateId;
        if (config is not null) data["config"] = config;
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = category,
            ["position"] = new JsonObject { ["x"] = x, ["y"] = y },
            ["data"] = data,
        };
    }

    private static JsonObject VerdictValue(string verdict, string policyId, int version) => new()
    {
        ["verdict"] = verdict,
        ["policyId"] = policyId,
        ["policyVersion"] = version,
    };
}
