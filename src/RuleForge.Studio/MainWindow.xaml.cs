using System.Windows;
using System.Windows.Controls;
using RuleForge.Studio.ViewModels;

namespace RuleForge.Studio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _viewModel.OnExplorerNodeSelected(e.NewValue as ExplorerNodeViewModel);

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
