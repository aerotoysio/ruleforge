using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Core.Models;
using RuleForge.Studio.Core.Connections;
using RuleForge.Studio.Core.Testing;
using Rule = RuleForge.Core.Models.Rule;

namespace RuleForge.Studio.ViewModels;

/// <summary>
/// Phase-1 shell: an Object Explorer over an <see cref="IRuleForgeConnection"/> (currently a local
/// workspace), a Nodify rule designer, a reference-set (datasource) viewer, and an in-process test
/// harness. The DocumentForge connection slots in behind the same interface next.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IRuleForgeConnection _connection;
    private readonly InProcessEvaluator _evaluator;
    private readonly string _scenariosDir;

    private Rule? _currentRule;

    // Rule → a representative sample request (from the engine's own scenarios).
    private static readonly Dictionary<string, string> SampleScenario = new()
    {
        ["rule-bag-policy"] = "s-bag-3pc-markup15.json",
        ["rule-pnr-taxes"] = "s-pnr-2pax.json",
        ["rule-tier-bonus"] = "s-gold-pax.json",
        ["rule-seat-assignments"] = "s-2j-2s-2p.json",
    };

    public ObservableCollection<ExplorerNodeViewModel> ExplorerRoots { get; } = new();

    [ObservableProperty] private RuleGraphViewModel? _graph;
    [ObservableProperty] private string _ruleHeader = "Select a rule or datasource in the Object Explorer.";
    [ObservableProperty] private string _requestJson = "{\n}\n";
    [ObservableProperty] private string _resultText = "";

    // Datasource (reference-set) view.
    [ObservableProperty] private bool _showReferenceView;
    [ObservableProperty] private string _referenceTitle = "";
    [ObservableProperty] private DataView? _referenceData;

    public MainViewModel()
    {
        var root = LocateFixturesRoot();
        _scenariosDir = Path.Combine(root, "scenarios");
        _connection = new LocalWorkspaceConnection(
            Path.Combine(root, "rules"),
            Path.Combine(root, "refs"),
            "Local workspace (fixtures)");
        _evaluator = new InProcessEvaluator(_connection.ReferenceSetSource);

        BuildExplorer();
    }

    private void BuildExplorer()
    {
        var conn = new ConnectionNodeViewModel { Name = _connection.DisplayName };

        // Rules — grouped by category when present, else a flat list.
        var rulesFolder = new FolderNodeViewModel { Name = "Rules" };
        var rules = _connection.ListRulesAsync().GetAwaiter().GetResult();
        foreach (var group in rules.GroupBy(r => r.Category).OrderBy(g => g.Key ?? "￿"))
        {
            var target = rulesFolder;
            if (group.Key is { } cat && !string.IsNullOrWhiteSpace(cat))
            {
                target = new FolderNodeViewModel { Name = cat };
                rulesFolder.Children.Add(target);
            }
            foreach (var r in group.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                target.Children.Add(new RuleNodeViewModel { Name = r.Name, Glyph = "▪", Rule = r });
        }
        conn.Children.Add(rulesFolder);

        // Reference sets (datasources).
        var refFolder = new FolderNodeViewModel { Name = "Reference sets (datasources)" };
        foreach (var rs in _connection.ListReferenceSetsAsync().GetAwaiter().GetResult())
            refFolder.Children.Add(new ReferenceSetNodeViewModel
            {
                Name = rs.Name,
                Glyph = "▤",
                ReferenceSet = rs,
            });
        conn.Children.Add(refFolder);

        // Placeholders for the homes we'll build out (Andrew: products/outputs/templates/datasources).
        conn.Children.Add(Placeholder("Products & templates"));
        conn.Children.Add(Placeholder("Schemas"));
        conn.Children.Add(Placeholder("Environments"));

        ExplorerRoots.Add(conn);
    }

    private static FolderNodeViewModel Placeholder(string name)
    {
        var folder = new FolderNodeViewModel { Name = name, IsExpanded = false };
        folder.Children.Add(new MessageNodeViewModel { Name = "(coming soon)" });
        return folder;
    }

    /// <summary>Called from the tree's SelectedItemChanged.</summary>
    public void OnExplorerNodeSelected(ExplorerNodeViewModel? node)
    {
        switch (node)
        {
            case RuleNodeViewModel r:
                ShowRule(r.Rule);
                break;
            case ReferenceSetNodeViewModel rs:
                ShowReferenceSet(rs.ReferenceSet);
                break;
        }
    }

    private void ShowRule(RuleSummary summary)
    {
        ShowReferenceView = false;
        ResultText = "";
        _currentRule = _connection.GetRuleAsync(summary.Id).GetAwaiter().GetResult();
        if (_currentRule is null)
        {
            Graph = null;
            RuleHeader = $"Could not load rule '{summary.Id}'.";
            return;
        }

        Graph = RuleGraphViewModel.FromRule(_currentRule);
        RuleHeader = $"{_currentRule.Name}   ·   {_currentRule.Method} {_currentRule.Endpoint}   ·   v{_currentRule.CurrentVersion}   ·   {_currentRule.Status}";
        RequestJson = LoadSample(_currentRule.Id);
    }

    private void ShowReferenceSet(ReferenceSetSummary summary)
    {
        var rs = _connection.GetReferenceSetAsync(summary.Id).GetAwaiter().GetResult();
        if (rs is null) return;

        var table = new DataTable();
        foreach (var col in rs.Columns)
            table.Columns.Add(col, typeof(string));
        foreach (var row in rs.Rows)
        {
            var dr = table.NewRow();
            foreach (var col in rs.Columns)
                dr[col] = row.TryGetValue(col, out var v) ? JsonToString(v) : "";
            table.Rows.Add(dr);
        }

        ReferenceData = table.DefaultView;
        ReferenceTitle = $"{rs.Name}   ·   {rs.Columns.Count} columns × {rs.Rows.Count} rows   ·   v{rs.CurrentVersion}";
        RuleHeader = ReferenceTitle;
        ShowReferenceView = true;
    }

    [RelayCommand]
    private void Run()
    {
        if (_currentRule is null) return;

        try
        {
            using var doc = JsonDocument.Parse(RequestJson);
            var envelope = _evaluator.Evaluate(_currentRule, doc.RootElement, debug: true);

            var sb = new StringBuilder();
            sb.AppendLine($"decision : {envelope.Decision}");
            sb.AppendLine($"version  : {envelope.RuleVersion}");
            sb.AppendLine();
            sb.AppendLine("result:");
            sb.AppendLine(Pretty(envelope.Result));
            sb.AppendLine();
            sb.AppendLine("trace:");
            if (envelope.Trace is { } trace)
                foreach (var t in trace)
                    sb.AppendLine($"  {t.Outcome,-6}  {t.NodeId}");
            ResultText = sb.ToString();
        }
        catch (JsonException jx)
        {
            ResultText = $"Request is not valid JSON:\n{jx.Message}";
        }
        catch (Exception ex)
        {
            ResultText = $"Evaluation error:\n{ex.Message}";
        }
    }

    private string LoadSample(string ruleId)
    {
        if (SampleScenario.TryGetValue(ruleId, out var file))
        {
            var path = Path.Combine(_scenariosDir, file);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        return "{\n}\n";
    }

    private static string JsonToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => e.GetRawText(),
    };

    private static string Pretty(JsonElement? element)
    {
        if (element is null) return "  (none)";
        return JsonSerializer.Serialize(element.Value, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Walk up from the running exe to find the engine's <c>fixtures</c> folder (demo-only).</summary>
    private static string LocateFixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "fixtures", "rules")))
                return Path.Combine(dir.FullName, "fixtures");
            dir = dir.Parent;
        }
        return @"C:\DATA\14. ruleForge\ruleforge-studio\fixtures";
    }
}
