using System.Windows;
using System.Windows.Input;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>Picks a GitHub repository to link to a project. Signing in lives in Settings.</summary>
public partial class GitHubDialog : Window
{
    private List<GitHubClient.Repo> _repos = [];

    private GitHubDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Shows the dialog; the chosen repository, or null if cancelled or not signed in.</summary>
    public static GitHubClient.Repo? Pick(Window? owner)
    {
        var dialog = new GitHubDialog { Owner = owner };

        return dialog.ShowDialog() == true && dialog.List.SelectedItem is GitHubClient.Repo repo
            ? repo
            : null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (!GitHubClient.IsConnected)
        {
            Message.Text = LocalizationService.T("L_GhNotConnected");
            Message.Visibility = Visibility.Visible;
            return;
        }

        LoginText.Text = GitHubClient.Login.Length > 0 ? "@" + GitHubClient.Login : string.Empty;
        Search.Visibility = Visibility.Visible;

        Message.Text = LocalizationService.T("L_GhLoading");
        Message.Visibility = Visibility.Visible;
        _repos = await GitHubClient.ListReposAsync().ConfigureAwait(true);
        Message.Visibility = _repos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_repos.Count == 0)
        {
            Message.Text = LocalizationService.T("L_GhNoRepos");
        }

        ApplyFilter();
    }

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = Search.Text.Trim();
        List.ItemsSource = query.Length == 0
            ? _repos
            : _repos.Where(r => r.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is GitHubClient.Repo)
        {
            DialogResult = true;
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is GitHubClient.Repo)
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
