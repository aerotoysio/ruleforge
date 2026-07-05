using System.IO;
using System.Windows;
using Microsoft.Win32;
using RuleForge.Studio.Core.Connections;
using RuleForge.Studio.Core.Settings;

namespace RuleForge.Studio.Views;

public partial class ConnectDialog : Window
{
    public ConnectionDescriptor? Descriptor { get; private set; }
    public string? ApiKey { get; private set; }

    public ConnectDialog()
    {
        InitializeComponent();
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (LocalPanel is null || DfPanel is null) return; // during init
        var local = LocalRadio.IsChecked == true;
        LocalPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        DfPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a rule workspace folder" };
        if (dlg.ShowDialog(this) == true)
            FolderBox.Text = dlg.FolderName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LocalRadio.IsChecked == true)
            {
                var dir = FolderBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    throw new Exception("Pick an existing workspace folder.");

                Descriptor = new ConnectionDescriptor
                {
                    Kind = RuleForgeConnectionKind.LocalWorkspace,
                    Name = FallbackName(NameBox.Text, new DirectoryInfo(dir).Name),
                    WorkspaceDir = dir,
                };
                ApiKey = null;
            }
            else
            {
                var url = UrlBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(url))
                    throw new Exception("Enter the DocumentForge URL.");

                Descriptor = new ConnectionDescriptor
                {
                    Kind = RuleForgeConnectionKind.DocumentForge,
                    Name = FallbackName(NameBox.Text, url),
                    Url = url,
                    Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? null : DatabaseBox.Text.Trim(),
                    Environment = string.IsNullOrWhiteSpace(EnvBox.Text) ? "staging" : EnvBox.Text.Trim(),
                    CollectionPrefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? null : PrefixBox.Text.Trim(),
                };
                ApiKey = string.IsNullOrEmpty(KeyBox.Password) ? null : KeyBox.Password;
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private static string FallbackName(string entered, string fallback)
        => string.IsNullOrWhiteSpace(entered) ? fallback : entered.Trim();
}
