using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>Confirms a GitHub publish or update: the repository name, its visibility, and the changes going out.</summary>
public partial class PublishDialog : Window
{
    private PublishDialog(bool isUpdate, string defaultName, string repoName, IReadOnlyList<GitOps.Change> changes)
    {
        InitializeComponent();

        TitleText.Text = LocalizationService.T(isUpdate ? "L_PubUpdateTitle" : "L_PubPublishTitle");
        OkButton.Content = LocalizationService.T(isUpdate ? "L_PublishUpdate" : "L_Publish");
        MessageBox.Text = isUpdate ? "Update from RedBloom" : "Publish from RedBloom";

        if (isUpdate)
        {
            NamePanel.Visibility = Visibility.Collapsed;
            RepoText.Visibility = Visibility.Visible;
            RepoText.Text = string.Format(LocalizationService.T("L_PubUpdating"), repoName);
        }
        else
        {
            NameBox.Text = defaultName;
        }

        ChangesHeader.Text = string.Format(LocalizationService.T("L_PubChanges"), changes.Count);
        ChangesList.ItemsSource = changes.Select(c => new Row(c.Code, c.Path, ColorFor(c.Code))).ToList();
        NoChanges.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ImportAllBox.Checked += (_, _) => UpdateHint();
        ImportAllBox.Unchecked += (_, _) => UpdateHint();
        UpdateHint();
    }

    private void UpdateHint() =>
        ImportHint.Text = LocalizationService.T(ImportAllBox.IsChecked == true ? "L_PubImportAllHint" : "L_PubMetaOnlyHint");

    /// <summary>What the user chose: the repository name, whether it is private, the commit message, and whether to publish every file (else only project data).</summary>
    public readonly record struct Result(string Name, bool Private, string Message, bool ImportAll);

    /// <summary>Shows the dialog; the chosen settings, or null if cancelled.</summary>
    public static Result? Show(Window? owner, bool isUpdate, string defaultName, string repoName, IReadOnlyList<GitOps.Change> changes)
    {
        var dialog = new PublishDialog(isUpdate, defaultName, repoName, changes) { Owner = owner };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var message = dialog.MessageBox.Text.Trim();
        return new Result(
            dialog.NameBox.Text.Trim(),
            dialog.PrivateBtn.IsChecked == true,
            message.Length > 0 ? message : dialog.MessageBox.Text,
            dialog.ImportAllBox.IsChecked == true);
    }

    private sealed record Row(string Code, string Path, Brush Brush);

    private static Brush ColorFor(string code) => (code.Length > 0 ? code[0] : ' ') switch
    {
        'A' or '?' => new SolidColorBrush(Color.FromRgb(0x5b, 0xb8, 0x5b)),
        'M' => new SolidColorBrush(Color.FromRgb(0xd8, 0xc1, 0x4a)),
        'D' => new SolidColorBrush(Color.FromRgb(0xe0, 0x55, 0x5f)),
        'R' or 'C' => new SolidColorBrush(Color.FromRgb(0x4a, 0xa6, 0xd8)),
        _ => new SolidColorBrush(Color.FromRgb(0xa9, 0xab, 0xb3)),
    };

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (NamePanel.Visibility == Visibility.Visible && NameBox.Text.Trim().Length == 0)
        {
            NameBox.Focus();
            return;
        }

        DialogResult = true;
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
