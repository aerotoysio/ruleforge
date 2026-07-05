using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RuleForge.Studio.ViewModels;
using RuleForge.Studio.Views;

namespace RuleForge.Studio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Frame a freshly-loaded rule graph once the containers are realised.
        _viewModel.FitRequested += () =>
            Dispatcher.BeginInvoke(() => Editor?.FitToScreen(null), DispatcherPriority.Background);

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _viewModel.OnExplorerNodeSelected(e.NewValue as ExplorerNodeViewModel);

    // ─── canvas: double-click a node → edit ──────────────────────────────────

    private void OnEditorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (FindNode(e.OriginalSource) is { } node)
            _viewModel.EditNodeCommand.Execute(node);
    }

    private static NodeViewModel? FindNode(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: NodeViewModel node }) return node;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ─── toolbox drag-and-drop → add node ────────────────────────────────────

    private void OnToolboxItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            && sender is FrameworkElement { DataContext: ToolboxItem item } fe)
        {
            DragDrop.DoDragDrop(fe, new DataObject(typeof(ToolboxItem), item), DragDropEffects.Copy);
        }
    }

    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ToolboxItem)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ToolboxItem)) is ToolboxItem item)
        {
            var loc = Editor.GetLocationInsideEditor(e);
            _viewModel.AddNodeAt(item.Category, loc.X, loc.Y);
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Descriptor is null) return;

        try
        {
            await _viewModel.AddConnectionAsync(dlg.Descriptor, dlg.ApiKey);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not connect:\n{ex.Message}", "RuleForge Studio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
