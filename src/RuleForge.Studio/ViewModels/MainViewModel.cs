using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuleForge.Core.Models;
using RuleForge.Studio.Core.Authoring;
using RuleForge.Studio.Core.Connections;
using RuleForge.Studio.Core.Settings;
using RuleForge.Studio.Core.Testing;
using RuleForge.Studio.Views;
using Rule = RuleForge.Core.Models.Rule;

namespace RuleForge.Studio.ViewModels;

/// <summary>
/// The application shell view-model: connections + Object Explorer, the connector-based Nodify
/// designer (interactive edges, per-node config dialogs, toolbox), a datasource viewer, and the
/// in-process test harness. The loaded <see cref="Rule"/> is the single source of truth; the canvas
/// is re-projected from it after every structural edit.
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

    /// <summary>Node types offered in the canvas toolbox.</summary>
    public IReadOnlyList<ToolboxItem> Toolbox { get; } = new[]
    {
        new ToolboxItem("Filter", NodeCategory.Filter, "#2563EB"),
        new ToolboxItem("Logic", NodeCategory.Logic, "#7C3AED"),
        new ToolboxItem("Product", NodeCategory.Product, "#059669"),
        new ToolboxItem("Mutator", NodeCategory.Mutator, "#D97706"),
        new ToolboxItem("Calc", NodeCategory.Calc, "#0891B2"),
        new ToolboxItem("Constant", NodeCategory.Constant, "#059669"),
        new ToolboxItem("Output", NodeCategory.Output, "#64748B"),
    };

    public ObservableCollection<ExplorerNodeViewModel> ExplorerRoots { get; } = new();

    [ObservableProperty] private GraphViewModel? _graph;
    [ObservableProperty] private string _ruleHeader = "Select a rule or datasource in the Object Explorer.";
    [ObservableProperty] private string _requestJson = "{\n}\n";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _statusText = "Ready";

    [ObservableProperty] private bool _showReferenceView;
    [ObservableProperty] private string _referenceTitle = "";
    [ObservableProperty] private DataView? _referenceData;

    /// <summary>Raised when a brand-new rule graph is loaded, so the view can fit it to screen.</summary>
    public event Action? FitRequested;

    public MainViewModel()
    {
        _workspace = new StudioWorkspace();
        var fixtures = LocateFixturesRoot();
        _scenariosDir = Path.Combine(fixtures, "scenarios");
        SeedFirstRun(fixtures);
    }

    // ─── connections / explorer ──────────────────────────────────────────────

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

    public async Task AddConnectionAsync(ConnectionDescriptor descriptor, string? apiKey)
    {
        var conn = ConnectionFactory.Create(descriptor, apiKey);
        await conn.ConnectAsync();
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
        var connNode = new ConnectionNodeViewModel { Name = conn.DisplayName, DescriptorId = descriptorId, Connection = conn };

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
            case RuleNodeViewModel r: ShowRule(r.Connection, r.Rule); break;
            case ReferenceSetNodeViewModel rs: ShowReferenceSet(rs.Connection, rs.ReferenceSet); break;
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

        LoadGraph();
        RuleHeader = $"{_currentRule.Name}   ·   {_currentRule.Method} {_currentRule.Endpoint}   ·   v{_currentRule.CurrentVersion}   ·   {_currentRule.Status}";
        RequestJson = LoadSample(_currentRule.Id);
        FitRequested?.Invoke();
    }

    private void ShowReferenceSet(IRuleForgeConnection conn, ReferenceSetSummary summary)
    {
        var rs = conn.GetReferenceSetAsync(summary.Id).GetAwaiter().GetResult();
        if (rs is null) return;

        var table = new DataTable();
        foreach (var col in rs.Columns) table.Columns.Add(col, typeof(string));
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

    // ─── canvas authoring ────────────────────────────────────────────────────

    private void LoadGraph()
    {
        var graph = GraphViewModel.FromRule(_currentRule!);
        graph.ConnectRequested += OnConnectRequested;
        graph.DisconnectRequested += OnDisconnectRequested;
        Graph = graph;
    }

    /// <summary>Re-project the canvas from the rule after an edit, keeping node positions.</summary>
    private void RebuildGraph()
    {
        SyncPositionsFromGraph();
        LoadGraph();
    }

    private void SyncPositionsFromGraph()
    {
        if (Graph is null || _currentRule is null) return;
        var pos = Graph.Nodes.ToDictionary(n => n.Id, n => (n.Location.X, n.Location.Y));
        _currentRule = GraphEditing.SyncPositions(_currentRule, pos);
    }

    private void OnConnectRequested(ConnectorViewModel output, ConnectorViewModel input)
    {
        if (_currentRule is null) return;
        _currentRule = GraphEditing.AddEdge(_currentRule, output.Node.Id, output.Branch, input.Node.Id);
        RebuildGraph();
        Run();
    }

    private void OnDisconnectRequested(ConnectionViewModel connection)
    {
        if (_currentRule is null) return;
        _currentRule = GraphEditing.RemoveEdge(_currentRule, connection.Source.Node.Id, connection.Target.Node.Id, connection.Source.Branch);
        RebuildGraph();
        Run();
    }

    /// <summary>Add a node at a graph-space location (used by toolbox drag-drop).</summary>
    public void AddNodeAt(NodeCategory category, double x, double y)
    {
        if (_currentRule is null)
        {
            StatusText = "Open a rule before adding nodes.";
            return;
        }
        SyncPositionsFromGraph();
        var (updated, newId) = GraphEditing.AddNode(_currentRule, category, x, y);
        _currentRule = updated;
        LoadGraph();

        if (category == NodeCategory.Filter)
            EditNode(Graph?.Nodes.FirstOrDefault(n => n.Id == newId));
    }

    [RelayCommand]
    private void AddFilter()
    {
        // Toolbar shortcut: drop a filter near the middle of the current view.
        AddNodeAt(NodeCategory.Filter, 240, 200);
    }

    [RelayCommand]
    private void EditNode(NodeViewModel? nodeVm)
    {
        if (nodeVm is null || _currentRule is null) return;
        var node = _currentRule.Nodes.FirstOrDefault(n => n.Id == nodeVm.Id);
        if (node is null) return;

        if (node.Data.Category != NodeCategory.Filter)
        {
            MessageBox.Show(Application.Current.MainWindow!,
                $"A settings editor for {node.Data.Category} nodes is coming next — the string filter is the first worked example.",
                "RuleForge Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var vm = BuildFilterInspector(node);
        var dlg = new FilterEditorDialog { DataContext = vm, Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && vm.SelectedField is not null)
        {
            var config = FilterEditing.ToConfig(FilterEditing.BuildStringFilter(vm.SelectedField.Path, vm.Operator, vm.Value));
            _currentRule = FilterEditing.ReplaceNodeConfig(_currentRule, node.Id, config);
            RebuildGraph();
            Run();
        }
    }

    private FilterInspectorViewModel BuildFilterInspector(RuleNode node)
    {
        var fields = SchemaFields.FromInputSchema(_currentRule!.InputSchema);
        var existing = FilterEditing.ReadStringFilter(node.Data.Config);
        var path = existing?.Source.Path;
        var opJson = existing is null ? "equals" : JsonName(existing.Compare.Operator);
        var val = existing?.Compare.Value
                  ?? (existing?.Compare.Values is { Count: > 0 } vs ? string.Join(", ", vs) : "");

        return new FilterInspectorViewModel
        {
            NodeId = node.Id,
            NodeLabel = node.Data.Label,
            Fields = fields,
            SelectedField = fields.FirstOrDefault(f => f.Path == path)
                            ?? fields.FirstOrDefault(f => f.Type == SchemaFieldType.String)
                            ?? fields.FirstOrDefault(),
            Operator = opJson,
            Value = val,
        };
    }

    // ─── test harness ────────────────────────────────────────────────────────

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
            if (File.Exists(path)) return File.ReadAllText(path);
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

    private static string JsonName(StringFilterOperator op)
        => JsonSerializer.Serialize(op, RuleForge.Core.AeroJson.Options).Trim('"');

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

/// <summary>A node type in the canvas toolbox.</summary>
public sealed class ToolboxItem
{
    public ToolboxItem(string name, NodeCategory category, string colorHex)
    {
        Name = name;
        Category = category;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        Color = brush;
    }

    public string Name { get; }
    public NodeCategory Category { get; }
    public Brush Color { get; }
}
