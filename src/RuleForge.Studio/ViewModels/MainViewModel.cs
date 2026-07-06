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

/// <summary>Which pane the centre of the workspace shows.</summary>
public enum CenterMode { Designer, Datasource, Tester }

/// <summary>
/// Application shell: connections + Object Explorer, the connector-based Nodify designer,
/// a reference-data (datasource) manager, an in-process test harness, and a standalone rule tester.
/// The loaded <see cref="Rule"/> is the single source of truth the canvas re-projects from.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly StudioWorkspace _workspace;
    private readonly string _scenariosDir;

    private Rule? _currentRule;
    private InProcessEvaluator? _evaluator;

    // reference-data editing
    private IRuleForgeConnection? _refConnection;
    private string? _refEditId;
    private int _refVersion = 1;

    private static readonly Dictionary<string, string> SampleScenario = new()
    {
        ["rule-bag-policy"] = "s-bag-3pc-markup15.json",
        ["rule-pnr-taxes"] = "s-pnr-2pax.json",
        ["rule-tier-bonus"] = "s-gold-pax.json",
        ["rule-seat-assignments"] = "s-2j-2s-2p.json",
    };

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
    public ObservableCollection<RuleNodeViewModel> AllRules { get; } = new();

    [ObservableProperty] private CenterMode _centerMode = CenterMode.Designer;

    [ObservableProperty] private GraphViewModel? _graph;
    [ObservableProperty] private string _ruleHeader = "Select a rule or datasource in the Object Explorer.";
    [ObservableProperty] private string _requestJson = "{\n}\n";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _statusText = "Ready";

    // datasource
    [ObservableProperty] private string _referenceTitle = "";
    [ObservableProperty] private string _referenceEditName = "";
    [ObservableProperty] private DataView? _referenceData;

    // standalone tester
    [ObservableProperty] private RuleNodeViewModel? _selectedTestRule;
    [ObservableProperty] private string _testerRequestJson = "{\n}\n";
    [ObservableProperty] private string _testerResultText = "";

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
                var node = await BuildConnectionNodeAsync(conn, descriptor.Id);
                ExplorerRoots.Add(node);
                CollectRules(node);
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
        var node = await BuildConnectionNodeAsync(conn, descriptor.Id);
        ExplorerRoots.Add(node);
        CollectRules(node);
        StatusText = $"Connected '{descriptor.Name}'.";
    }

    private void CollectRules(ExplorerNodeViewModel node)
    {
        if (node is RuleNodeViewModel r) AllRules.Add(r);
        foreach (var child in node.Children) CollectRules(child);
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
        connNode.Children.Add(await BuildReferenceFolderAsync(conn));
        connNode.Children.Add(Placeholder("Products & templates"));
        connNode.Children.Add(Placeholder("Schemas"));
        connNode.Children.Add(Placeholder("Environments"));
        return connNode;
    }

    private static async Task<FolderNodeViewModel> BuildReferenceFolderAsync(IRuleForgeConnection conn)
    {
        var refFolder = new FolderNodeViewModel { Name = "Reference sets (datasources)" };
        foreach (var rs in await conn.ListReferenceSetsAsync())
            refFolder.Children.Add(new ReferenceSetNodeViewModel { Name = rs.Name, Glyph = "▤", ReferenceSet = rs, Connection = conn });
        return refFolder;
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
        CenterMode = CenterMode.Designer;
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

    // ─── reference data (datasources) ─────────────────────────────────────────

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

        _refConnection = conn;
        _refEditId = rs.Id;
        _refVersion = rs.CurrentVersion;
        ReferenceEditName = rs.Name;
        ReferenceData = table.DefaultView;
        ReferenceTitle = $"{rs.Name}   ·   {rs.Columns.Count} columns × {rs.Rows.Count} rows   ·   v{rs.CurrentVersion}";
        RuleHeader = ReferenceTitle;
        CenterMode = CenterMode.Datasource;
    }

    /// <summary>Start a new datasource (key-value list or lookup table) in the editor.</summary>
    public void NewReferenceSet(bool keyValue)
    {
        _refConnection = ExplorerRoots
            .OfType<ConnectionNodeViewModel>()
            .Select(c => c.Connection)
            .FirstOrDefault(c => c.Capabilities.HasFlag(RuleForgeCapabilities.WriteReferenceSets));

        if (_refConnection is null)
        {
            StatusText = "No connected workspace can store reference data.";
            return;
        }

        var table = new DataTable();
        if (keyValue) { table.Columns.Add("key", typeof(string)); table.Columns.Add("value", typeof(string)); }
        else { table.Columns.Add("column1", typeof(string)); table.Columns.Add("column2", typeof(string)); }

        _refEditId = null;
        _refVersion = 1;
        ReferenceEditName = keyValue ? "New key-value list" : "New lookup table";
        ReferenceData = table.DefaultView;
        ReferenceTitle = "New datasource — edit cells, then Save";
        CenterMode = CenterMode.Datasource;
    }

    public void AddReferenceColumn(string name)
    {
        if (ReferenceData?.Table is { } table && !table.Columns.Contains(name))
            table.Columns.Add(name, typeof(string));
    }

    public void ImportReferenceCsv(string csv)
    {
        var (columns, rows) = ReferenceEditing.ParseCsv(csv);
        if (columns.Count == 0) { StatusText = "CSV had no header row."; return; }

        var table = new DataTable();
        foreach (var c in columns) table.Columns.Add(c, typeof(string));
        foreach (var r in rows)
        {
            var dr = table.NewRow();
            foreach (var c in columns) dr[c] = r.TryGetValue(c, out var v) ? v : "";
            table.Rows.Add(dr);
        }
        ReferenceData = table.DefaultView;
        StatusText = $"Imported {rows.Count} rows × {columns.Count} columns.";
    }

    [RelayCommand]
    private async Task SaveReferenceSet()
    {
        if (_refConnection is null || ReferenceData?.Table is not { } table) return;
        if (string.IsNullOrWhiteSpace(ReferenceEditName)) { StatusText = "Give the datasource a name first."; return; }

        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var rows = table.Rows.Cast<DataRow>()
            .Where(r => r.RowState != DataRowState.Deleted)
            .Select(r => (IReadOnlyDictionary<string, string?>)columns.ToDictionary(c => c, c => r[c]?.ToString()))
            .ToList();

        var id = _refEditId ?? ReferenceEditing.SlugId(ReferenceEditName);
        var set = ReferenceEditing.Build(id, ReferenceEditName.Trim(), columns, rows, _refVersion);

        try
        {
            await _refConnection.SaveReferenceSetAsync(set);
            _refEditId = id;
            ReferenceTitle = $"{set.Name}   ·   {columns.Count} columns × {rows.Count} rows   ·   v{_refVersion}";
            await RefreshReferenceFolderAsync(_refConnection);
            StatusText = $"Saved datasource '{set.Name}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save datasource: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteReferenceSet()
    {
        if (_refConnection is null || _refEditId is null) return;
        if (MessageBox.Show(Application.Current.MainWindow!, $"Delete datasource '{ReferenceEditName}'?",
                "RuleForge Studio", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        try
        {
            await _refConnection.DeleteReferenceSetAsync(_refEditId);
            await RefreshReferenceFolderAsync(_refConnection);
            StatusText = $"Deleted datasource '{ReferenceEditName}'.";
            ReferenceData = null;
            CenterMode = CenterMode.Designer;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not delete datasource: {ex.Message}";
        }
    }

    private async Task RefreshReferenceFolderAsync(IRuleForgeConnection conn)
    {
        var connNode = ExplorerRoots.OfType<ConnectionNodeViewModel>().FirstOrDefault(c => ReferenceEquals(c.Connection, conn));
        var folder = connNode?.Children.OfType<FolderNodeViewModel>().FirstOrDefault(f => f.Name.StartsWith("Reference sets"));
        if (folder is null) return;

        folder.Children.Clear();
        foreach (var rs in await conn.ListReferenceSetsAsync())
            folder.Children.Add(new ReferenceSetNodeViewModel { Name = rs.Name, Glyph = "▤", ReferenceSet = rs, Connection = conn });
    }

    // ─── canvas authoring ────────────────────────────────────────────────────

    private void LoadGraph()
    {
        var graph = GraphViewModel.FromRule(_currentRule!);
        graph.ConnectRequested += OnConnectRequested;
        graph.DisconnectRequested += OnDisconnectRequested;
        Graph = graph;
    }

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

    public void AddNodeAt(NodeCategory category, double x, double y)
    {
        if (_currentRule is null) { StatusText = "Open a rule before adding nodes."; return; }
        SyncPositionsFromGraph();
        var (updated, newId) = GraphEditing.AddNode(_currentRule, category, x, y);
        _currentRule = updated;
        LoadGraph();
        if (category == NodeCategory.Filter)
            EditNode(Graph?.Nodes.FirstOrDefault(n => n.Id == newId));
    }

    [RelayCommand]
    private void AddFilter() => AddNodeAt(NodeCategory.Filter, 240, 200);

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
            var config = FilterEditing.ToConfig(FilterEditing.BuildStringFilter(
                vm.SelectedField.Path, vm.Operator, vm.EffectiveValues,
                vm.ArrayMode.Value, vm.MissingMode.Value, vm.CaseInsensitive, vm.Trim));

            _currentRule = FilterEditing.UpdateNode(_currentRule, node.Id, vm.Label, vm.Description, config);
            RebuildGraph();
            Run();
        }
    }

    private FilterInspectorViewModel BuildFilterInspector(RuleNode node)
    {
        // Offer string elements (plus unknown-typed, so odd schemas stay usable).
        var fields = SchemaFields.FromInputSchema(_currentRule!.InputSchema)
            .Where(f => f.Type is SchemaFieldType.String or SchemaFieldType.Unknown)
            .ToList();

        var existing = FilterEditing.ReadStringFilter(node.Data.Config);
        var path = existing?.Source.Path;

        var vm = new FilterInspectorViewModel
        {
            NodeId = node.Id,
            Fields = fields,
            Label = node.Data.Label,
            Description = node.Data.Description ?? "",
            SelectedField = fields.FirstOrDefault(f => f.Path == path) ?? fields.FirstOrDefault(),
            Operator = existing is null ? "equals" : FilterEditing.JsonName(existing.Compare.Operator),
            Value = existing?.Compare.Value ?? "",
            CaseInsensitive = existing?.Compare.CaseInsensitive ?? true,
            Trim = existing?.Compare.Trim ?? true,
            ArrayMode = FilterInspectorViewModel.ArrayModeOptions.FirstOrDefault(
                    o => existing is not null && o.Value == FilterEditing.JsonName(existing.ArraySelector))
                ?? FilterInspectorViewModel.ArrayModeOptions[0],
            MissingMode = FilterInspectorViewModel.MissingModeOptions.FirstOrDefault(
                    o => existing is not null && o.Value == FilterEditing.JsonName(existing.OnMissing))
                ?? FilterInspectorViewModel.MissingModeOptions[0],
        };

        if (existing?.Compare.Values is { } values)
            foreach (var v in values)
                vm.Values.Add(v);

        return vm;
    }

    // ─── test harness (designer side-panel) ───────────────────────────────────

    [RelayCommand]
    private void Run()
    {
        if (_currentRule is null || _evaluator is null) return;
        ResultText = Evaluate(_evaluator, _currentRule, RequestJson);
    }

    // ─── standalone rule tester ───────────────────────────────────────────────

    [RelayCommand]
    private void OpenTester() => CenterMode = CenterMode.Tester;

    partial void OnSelectedTestRuleChanged(RuleNodeViewModel? value)
    {
        TesterResultText = "";
        if (value is not null) TesterRequestJson = LoadSample(value.Rule.Id);
    }

    [RelayCommand]
    private void RunTester()
    {
        if (SelectedTestRule is null) { TesterResultText = "Pick a rule to test."; return; }
        var conn = SelectedTestRule.Connection;
        var rule = conn.GetRuleAsync(SelectedTestRule.Rule.Id).GetAwaiter().GetResult();
        if (rule is null) { TesterResultText = "Could not load the rule."; return; }
        TesterResultText = Evaluate(new InProcessEvaluator(conn.ReferenceSetSource), rule, TesterRequestJson);
    }

    private static string Evaluate(InProcessEvaluator evaluator, Rule rule, string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var envelope = evaluator.Evaluate(rule, doc.RootElement, debug: true);

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
            return sb.ToString();
        }
        catch (JsonException jx)
        {
            return $"Request is not valid JSON:\n{jx.Message}";
        }
        catch (Exception ex)
        {
            return $"Evaluation error:\n{ex.Message}";
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

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
