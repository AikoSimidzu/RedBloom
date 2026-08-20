using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>
/// The browser sign-in to GitHub (OAuth device flow): asks GitHub for a short code, shows it, opens
/// the verification page, and polls until the user authorises — then the window closes with success.
/// No token is ever typed here.
/// </summary>
public partial class GitHubSignInDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private GitHubClient.DeviceCode? _code;

    private GitHubSignInDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>Runs the sign-in. Returns true when the account is connected.</summary>
    public static bool Show(Window? owner)
    {
        var dialog = new GitHubSignInDialog { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StatusText.Text = LocalizationService.T("L_GhDeviceStarting");

        var (code, error) = await GitHubClient.StartDeviceAsync(_cts.Token).ConfigureAwait(true);

        if (error is not null || code is not { } got)
        {
            Fail(error ?? "GitHub did not return a code.");
            return;
        }

        _code = got;
        CodeBox.Text = got.UserCode;
        CodePanel.Visibility = Visibility.Visible;
        OpenButton.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.T("L_GhDeviceWaiting");

        // Open the page straight away, so the code is one paste from done.
        OpenVerification();

        string? result;
        try
        {
            result = await GitHubClient.PollDeviceAsync(got, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (result is null)
        {
            DialogResult = true;
            return;
        }

        Fail(result);
    }

    private void Fail(string message)
    {
        CodePanel.Visibility = Visibility.Collapsed;
        OpenButton.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        CloseButton.Content = LocalizationService.T("L_Close");
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenVerification();

    private void OpenVerification()
    {
        if (_code is not { } code)
        {
            return;
        }

        try
        {
            Clipboard.SetText(code.UserCode);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The clipboard was busy; the code is still on screen to copy by hand.
        }

        var uri = code.VerificationUri.Length > 0 ? code.VerificationUri : "https://github.com/login/device";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not open the verification page: {ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
