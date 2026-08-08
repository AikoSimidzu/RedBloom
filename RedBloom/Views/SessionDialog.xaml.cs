using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>Add/edit form for a saved SSH session.</summary>
public partial class SessionDialog : Window
{
    private readonly SshSession _session;
    private readonly ObservableCollection<PortForwardRow> _forwards = [];

    private SessionDialog(SshSession session, bool isNew)
    {
        _session = session;

        InitializeComponent();

        ForwardList.ItemsSource = _forwards;
        foreach (var forward in session.Forwards)
        {
            _forwards.Add(new PortForwardRow(forward));
        }

        HeaderText.Text = LocalizationService.T(isNew ? "L_NewSshSessionHeader" : "L_EditSshSessionHeader");
        NameBox.Text = session.Name;
        HostBox.Text = session.Host;
        PortBox.Text = session.Port.ToString();
        UserBox.Text = session.Username;
        AutoReconnectBox.IsChecked = session.AutoReconnect;

        if (session.AuthKind == SshAuthKind.PrivateKey)
        {
            KeyAuth.IsChecked = true;
            KeyPathBox.Text = session.PrivateKeyPath ?? string.Empty;
            PassphraseField.Password = session.Secret ?? string.Empty;
        }
        else
        {
            PasswordAuth.IsChecked = true;
            PasswordField.Password = session.Secret ?? string.Empty;
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Shows the dialog and writes the edits back into <paramref name="session"/>.</summary>
    /// <returns><c>true</c> if the user saved.</returns>
    public static bool Edit(Window owner, SshSession session, bool isNew)
    {
        var dialog = new SessionDialog(session, isNew) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void AuthKind_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the panels exist.
        if (PasswordPanel is null || KeyPanel is null)
        {
            return;
        }

        var usesKey = KeyAuth.IsChecked == true;
        PasswordPanel.Visibility = usesKey ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = usesKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a private key",
            CheckFileExists = true,
            Filter = "Private keys (*.pem;*.key;id_*)|*.pem;*.key;id_*|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };

        if (dialog.ShowDialog(this) == true)
        {
            KeyPathBox.Text = dialog.FileName;
        }
    }

    private void AddForward_Click(object sender, RoutedEventArgs e) =>
        _forwards.Add(new PortForwardRow());

    private void RemoveForward_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PortForwardRow row })
        {
            _forwards.Remove(row);
        }
    }

    /// <summary>
    /// Fills the form from an ssh command line on the clipboard, so an invocation someone
    /// already has written down can be reused as-is.
    /// </summary>
    private void PasteSshCommand_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            ShowError(LocalizationService.T("L_ErrClipboardRead"));
            return;
        }

        if (text.Length == 0)
        {
            ShowError(LocalizationService.T("L_ErrClipboardEmpty"));
            return;
        }

        if (!SshCommandParser.TryParse(text, out var parsed, out var error))
        {
            ShowError(error ?? LocalizationService.T("L_ErrNotSshCommand"));
            return;
        }

        NameBox.Text = parsed.Host;
        HostBox.Text = parsed.Host;
        PortBox.Text = parsed.Port.ToString();
        UserBox.Text = parsed.Username;

        if (parsed.AuthKind == SshAuthKind.PrivateKey)
        {
            KeyAuth.IsChecked = true;
            KeyPathBox.Text = parsed.PrivateKeyPath ?? string.Empty;
        }

        _forwards.Clear();
        foreach (var forward in parsed.Forwards)
        {
            _forwards.Add(new PortForwardRow(forward));
        }

        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            ShowError(LocalizationService.T("L_ErrHostRequired"));
            HostBox.Focus();
            return;
        }

        var user = UserBox.Text.Trim();
        if (user.Length == 0)
        {
            ShowError(LocalizationService.T("L_ErrUserRequired"));
            UserBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError(LocalizationService.T("L_ErrPortRange"));
            PortBox.Focus();
            return;
        }

        var usesKey = KeyAuth.IsChecked == true;
        if (usesKey)
        {
            var keyPath = KeyPathBox.Text.Trim();
            if (keyPath.Length == 0)
            {
                ShowError(LocalizationService.T("L_ErrChooseKey"));
                return;
            }

            if (!File.Exists(keyPath))
            {
                ShowError(LocalizationService.T("L_ErrKeyMissing"));
                return;
            }

            _session.PrivateKeyPath = keyPath;
            _session.Secret = PassphraseField.Password;
        }
        else
        {
            _session.Secret = PasswordField.Password;
        }

        var forwards = new List<PortForward>(_forwards.Count);
        foreach (var row in _forwards)
        {
            if (!row.TryBuild(out var forward, out var forwardError))
            {
                ShowError(forwardError ?? LocalizationService.T("L_ErrTunnelIncomplete"));
                return;
            }

            forwards.Add(forward);
        }

        var duplicate = forwards
            .GroupBy(f => (f.BoundHost, f.BoundPort))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            ShowError(string.Format(LocalizationService.T("L_ErrTunnelDuplicate"),
                $"{duplicate.Key.BoundHost}:{duplicate.Key.BoundPort}"));
            return;
        }

        _session.Forwards = forwards;
        _session.AutoReconnect = AutoReconnectBox.IsChecked == true;

        var name = NameBox.Text.Trim();
        _session.Name = name.Length > 0 ? name : host;
        _session.Host = host;
        _session.Username = user;
        _session.Port = port;
        _session.AuthKind = usesKey ? SshAuthKind.PrivateKey : SshAuthKind.Password;

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
