using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using RuleForge.Studio.Core.Testing;

namespace RuleForge.Studio.ViewModels;

/// <summary>
/// Phase-0 demo shell: lists the engine's fixture rules, renders the selected rule on the
/// Nodify canvas, and runs it in-process through the real engine so you can see a live result +
/// per-node trace. This is a vertical slice — the real DocumentForge connection + authoring
/// come in later phases.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly string _rulesDir;
    private readonly LocalFileRuleSource _ruleSource;
    private readonly InProcessEvaluator _evaluator;

    // Rule → a representative sample request (from the engine's own scenarios).
    private static readonly Dictionary<string, string> SampleScenario = new()
    {
        ["rule-bag-policy"] = "s-bag-3pc-markup15.json",
        ["rule-pnr-taxes"] = "s-pnr-2pax.json",
        ["rule-tier-bonus"] = "s-gold-pax.json",
        ["rule-seat-assignments"] = "s-2j-2s-2p.json",
    };

    public ObservableCollection<RuleListItem> Rules { get; } = new();

    [ObservableProperty] private RuleListItem? _selectedRule;
    [ObservableProperty] private RuleGraphViewModel? _graph;
    [ObservableProperty] private string _ruleHeader = "Select a rule to view its graph.";
    [ObservableProperty] private string _requestJson = "{\n}\n";
    [ObservableProperty] private string _resultText = "";

    public MainViewModel()
    {
        var root = LocateFixturesRoot();
        _rulesDir = Path.Combine(root, "rules");
        var refsDir = Path.Combine(root, "refs");

        _ruleSource = new LocalFileRuleSource(_rulesDir);
        _evaluator = new InProcessEvaluator(new LocalFileReferenceSetSource(refsDir));

        foreach (var id in DiscoverRuleIds(_rulesDir))
        {
            var rule = _ruleSource.GetByIdAsync(id, null).GetAwaiter().GetResult();
            if (rule is not null)
                Rules.Add(new RuleListItem(rule));
        }

        SelectedRule = Rules.FirstOrDefault();
    }

    partial void OnSelectedRuleChanged(RuleListItem? value)
    {
        ResultText = "";
        if (value is null)
        {
            Graph = null;
            RuleHeader = "Select a rule to view its graph.";
            return;
        }

        var rule = value.Rule;
        Graph = RuleGraphViewModel.FromRule(rule);
        RuleHeader = $"{rule.Name}   ·   {rule.Method} {rule.Endpoint}   ·   v{rule.CurrentVersion}   ·   {rule.Status}";
        RequestJson = LoadSample(rule.Id);
    }

    [RelayCommand]
    private void Run()
    {
        if (SelectedRule is null) return;

        try
        {
            using var doc = JsonDocument.Parse(RequestJson);
            var envelope = _evaluator.Evaluate(SelectedRule.Rule, doc.RootElement, debug: true);

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
            var path = Path.Combine(Path.GetDirectoryName(_rulesDir)!, "scenarios", file);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        return "{\n}\n";
    }

    private static IEnumerable<string> DiscoverRuleIds(string rulesDir)
    {
        if (!Directory.Exists(rulesDir)) return [];
        return Directory.EnumerateFiles(rulesDir, "*.v*.json")
            .Select(f => Path.GetFileName(f))
            .Select(name =>
            {
                var idx = name.IndexOf(".v", StringComparison.Ordinal);
                return idx > 0 ? name[..idx] : name;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
    }

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
            var candidate = Path.Combine(dir.FullName, "fixtures", "rules");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "fixtures");
            dir = dir.Parent;
        }
        // Fallback to the known worktree location.
        return @"C:\DATA\14. ruleForge\ruleforge-studio\fixtures";
    }
}

/// <summary>Object-explorer entry for a rule.</summary>
public sealed class RuleListItem
{
    public RuleListItem(Rule rule) => Rule = rule;
    public Rule Rule { get; }
    public string Display => $"{Rule.Name}";
    public override string ToString() => Display;
}
