using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Studio.Core.Connections;
using RuleForge.Studio.Core.Settings;
using RuleForge.Studio.Core.Testing;
using Rule = RuleForge.Core.Models.Rule;

namespace RuleForge.Studio.ViewModels;

/// <summary>
/// Phase-1 shell: multiple connections (local workspace / DocumentForge) persisted under
/// %AppData%\RuleForge Studio, a hierarchical Object Explorer, a Nodify rule designer, a datasource
/// viewer, and an in-process test harness. Rules/datasources are read through IRuleForgeConnection.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly StudioWorkspace _workspace;
    private readonly string _scenariosDir;

    private Rule? _currentRule;
    private InProcessEvaluator? _evaluator;

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
    [ObservableProperty] private string _statusText = "Ready";

    [ObservableProperty] private bool _showReferenceView;
    [ObservableProperty] private string _referenceTitle = "";
    [ObservableProperty] private DataView? _referenceData;

    public MainViewModel()
    {
        _workspace = new StudioWorkspace();
        var fixtures = LocateFixturesRoot();
        _scenariosDir = Path.Combine(fixtures, "scenarios");
        SeedFirstRun(fixtures);
    }

    /// <summary>Called from MainWindow.Loaded — reconnects saved connections.</summary>
    public async Task InitializeAsync()
    {
        if (!_workspace.Settings.ReconnectOnStartup) return;

        foreach (var descriptor in _workspace.Connections.OrderByDescending(c => c.LastConnectedUtc))
        {
            try
            {
                var conn = _workspace.CreateConnection(descriptor);
                await conn.ConnectAsync();
                ExplorerRoots.Add(await BuildConnectionNodeAsync(conn, descriptor.Id));
                _workspace.TouchLastConnected(descriptor.Id);
            }
            catch (Exception ex)
            {
                StatusText = $"Could not connect '{descriptor.Name}': {ex.Message}";
            }
        }
    }

    /// <summary>Add + connect a new connection (dialog handled in the view).</summary>
    public async Task AddConnectionAsync(ConnectionDescriptor descriptor, string? apiKey)
    {
        var conn = ConnectionFactory.Create(descriptor, apiKey);
        await conn.ConnectAsync(); // throws on failure → surfaced by the caller
        _workspace.UpsertConnection(descriptor, apiKey);
        _workspace.TouchLastConnected(descriptor.Id);
        ExplorerRoots.Add(await BuildConnectionNodeAsync(conn, descriptor.Id));
        StatusText = $"Connected '{descriptor.Name}'.";
    }

    private void SeedFirstRun(string fixturesRoot)
    {
        if (_workspace.Connections.Count > 0) return;
        _workspace.UpsertConnection(new ConnectionDescriptor
        {
            Kind = RuleForgeConnectionKind.LocalWorkspace,
            Name = "Local workspace (fixtures)",
            WorkspaceDir = fixturesRoot,
        });
    }

    private static async Task<ConnectionNodeViewModel> BuildConnectionNodeAsync(IRuleForgeConnection conn, string descriptorId)
    {
        var connNode = new ConnectionNodeViewModel
        {
            Name = conn.DisplayName,
            DescriptorId = descriptorId,
            Connection = conn,
        };

        var rulesFolder = new FolderNodeViewModel { Name = "Rules" };
        var rules = await conn.ListRulesAsync();
        foreach (var group in rules.GroupBy(r => r.Category).OrderBy(g => g.Key ?? "￿"))
        {
            var target = rulesFolder;
            if (group.Key is { } cat && !string.IsNullOrWhiteSpace(cat))
            {
                target = new FolderNodeViewModel { Name = cat };
                rulesFolder.Children.Add(target);
            }
            foreach (var r in group.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                target.Children.Add(new RuleNodeViewModel { Name = r.Name, Glyph = "▪", Rule = r, Connection = conn });
        }
        connNode.Children.Add(rulesFolder);

        var refFolder = new FolderNodeViewModel { Name = "Reference sets (datasources)" };
        foreach (var rs in await conn.ListReferenceSetsAsync())
            refFolder.Children.Add(new ReferenceSetNodeViewModel { Name = rs.Name, Glyph = "▤", ReferenceSet = rs, Connection = conn });
        connNode.Children.Add(refFolder);

        connNode.Children.Add(Placeholder("Products & templates"));
        connNode.Children.Add(Placeholder("Schemas"));
        connNode.Children.Add(Placeholder("Environments"));
        return connNode;
    }

    private static FolderNodeViewModel Placeholder(string name)
    {
        var folder = new FolderNodeViewModel { Name = name, IsExpanded = false };
        folder.Children.Add(new MessageNodeViewModel { Name = "(coming soon)" });
        return folder;
    }

    public void OnExplorerNodeSelected(ExplorerNodeViewModel? node)
    {
        switch (node)
        {
            case RuleNodeViewModel r:
                ShowRule(r.Connection, r.Rule);
                break;
            case ReferenceSetNodeViewModel rs:
                ShowReferenceSet(rs.Connection, rs.ReferenceSet);
                break;
        }
    }

    private void ShowRule(IRuleForgeConnection conn, RuleSummary summary)
    {
        ShowReferenceView = false;
        ResultText = "";
        _currentRule = conn.GetRuleAsync(summary.Id).GetAwaiter().GetResult();
        _evaluator = new InProcessEvaluator(conn.ReferenceSetSource);

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

    private void ShowReferenceSet(IRuleForgeConnection conn, ReferenceSetSummary summary)
    {
        var rs = conn.GetReferenceSetAsync(summary.Id).GetAwaiter().GetResult();
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
        if (_currentRule is null || _evaluator is null) return;

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
        => element is null ? "  (none)" : JsonSerializer.Serialize(element.Value, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Walk up from the running exe to find the engine's <c>fixtures</c> folder (demo seed).</summary>
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
