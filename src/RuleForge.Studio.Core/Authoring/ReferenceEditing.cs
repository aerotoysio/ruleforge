using System.Globalization;
using System.Text.Json;
using RuleForge.Core.Loader;

namespace RuleForge.Studio.Core.Authoring;

/// <summary>
/// Build and import reference data (datasources). Cells are typed on the way in (numbers/booleans
/// stay numbers/booleans, everything else is a string) so engine lookups compare correctly.
/// </summary>
public static class ReferenceEditing
{
    public static ReferenceSet Build(
        string id, string name, IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyDictionary<string, string?>> rows, int currentVersion = 1)
    {
        var built = rows
            .Select(r => (IReadOnlyDictionary<string, JsonElement>)columns.ToDictionary(
                c => c,
                c => CellToJson(r.TryGetValue(c, out var v) ? v : null)))
            .ToList();

        return new ReferenceSet(id, name, columns.ToList(), built, currentVersion);
    }

    public static JsonElement CellToJson(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length > 0 && (bool.TryParse(t, out _) || IsNumber(t)))
        {
            using var doc = JsonDocument.Parse(t.ToLowerInvariant());
            return doc.RootElement.Clone();
        }
        return JsonSerializer.SerializeToElement(raw ?? "");
    }

    /// <summary>Parse simple CSV (first row = headers). Handles quoted fields containing commas.</summary>
    public static (List<string> Columns, List<Dictionary<string, string>> Rows) ParseCsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n').Where(l => l.Trim().Length > 0).ToList();

        var columns = lines.Count > 0 ? SplitCsvLine(lines[0]) : new List<string>();
        var rows = new List<Dictionary<string, string>>();

        for (var i = 1; i < lines.Count; i++)
        {
            var cells = SplitCsvLine(lines[i]);
            var row = new Dictionary<string, string>();
            for (var c = 0; c < columns.Count; c++)
                row[columns[c]] = c < cells.Count ? cells[c] : "";
            rows.Add(row);
        }

        return (columns, rows);
    }

    /// <summary>Slugify a display name into a stable reference-set id (e.g. "Cabin classes" → "ref-cabin-classes").</summary>
    public static string SlugId(string name)
    {
        var slug = new string((name ?? "").Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length == 0) slug = "reference";
        return slug.StartsWith("ref-") ? slug : "ref-" + slug;
    }

    private static bool IsNumber(string s)
        => long.TryParse(s, out _) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }
}
