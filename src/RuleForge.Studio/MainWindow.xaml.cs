using System.Windows;
using RuleForge.Studio.ViewModels;

namespace RuleForge.Studio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
