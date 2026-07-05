using System.Windows;
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
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _viewModel.OnExplorerNodeSelected(e.NewValue as ExplorerNodeViewModel);

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
