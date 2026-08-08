using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RedBloom.Services;
using RedBloom.Terminal;

namespace RedBloom.Views;

/// <summary>
/// Asks the user whether to trust a server's host key: the trust-on-first-use prompt, and
/// the considerably louder warning when a previously trusted key has changed.
/// </summary>
public partial class HostKeyPrompt : Window
{
    private HostKeyDecision _decision = HostKeyDecision.Reject;

    private HostKeyPrompt(SshHostKey key, HostKeyStatus status, KnownHost? stored)
    {
        InitializeComponent();

        EndpointText.Text = key.Endpoint;
        AlgorithmText.Text = key.KeyLength > 0
            ? $"{key.Algorithm} · {key.KeyLength} bit"
            : key.Algorithm;
        FingerprintText.Text = key.DisplayFingerprint;

        switch (status)
        {
            case HostKeyStatus.Changed:
                ConfigureForChangedKey(stored);
                break;

            case HostKeyStatus.NewAlgorithm:
                ConfigureForNewAlgorithm();
                break;

            default:
                ConfigureForFirstContact();
                break;
        }
    }

    /// <summary>Must be called on the UI thread.</summary>
    public static HostKeyDecision Ask(Window owner, SshHostKey key, HostKeyStatus status, KnownHost? stored)
    {
        var prompt = new HostKeyPrompt(key, status, stored) { Owner = owner };
        prompt.ShowDialog();
        return prompt._decision;
    }

    private void ConfigureForFirstContact()
    {
        HeaderText.Text = "Unknown server";
        BodyText.Text = $"RedBloom has not connected to {EndpointText.Text} before, so it cannot tell "
                        + "whether this is the right machine. Check the fingerprint against the server "
                        + "before trusting it.";
        HintText.Text = "On the server, `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` prints the "
                        + "fingerprint it should be showing.";
        AcceptStoreButton.IsDefault = true;
    }

    private void ConfigureForNewAlgorithm()
    {
        HeaderIcon.Text = "";
        HeaderText.Text = "New key type for a known host";
        BodyText.Text = $"{EndpointText.Text} is already trusted, but has never presented a key of this "
                        + "type before. That is normal after a server reconfiguration — and is also what "
                        + "an interception attempt would look like. Verify the fingerprint.";
        HintText.Text = "If you did not change anything on the server, do not trust this key.";
        AcceptStoreButton.IsDefault = true;
    }

    private void ConfigureForChangedKey(KnownHost? stored)
    {
        HeaderIcon.Text = "";
        HeaderIcon.Foreground = (Brush)FindResource("Danger");
        HeaderText.Text = "Host key has changed";
        HeaderText.Foreground = (Brush)FindResource("Danger");
        RootBorder.BorderBrush = (Brush)FindResource("Danger");

        BodyText.Text = $"The key presented by {EndpointText.Text} does not match the one RedBloom "
                        + "trusted previously. This happens when a server is rebuilt or its keys are "
                        + "rotated — but it is also exactly what a machine-in-the-middle attack looks "
                        + "like. Do not continue unless you know why the key changed.";
        HintText.Text = "Anything you type — including passwords — would go to whoever holds this key.";

        if (stored is not null)
        {
            StoredPanel.Visibility = Visibility.Visible;
            StoredFingerprintText.Text = $"SHA256:{stored.Sha256Fingerprint}";
        }

        // One deliberate choice only: no "just this once" escape hatch for a changed key.
        AcceptOnceButton.Visibility = Visibility.Collapsed;
        AcceptStoreButton.Content = "Replace stored key";
        AcceptStoreButton.Style = (Style)FindResource("GhostButton");
        RejectButton.Content = "Cancel connection";
        RejectButton.Style = (Style)FindResource("AccentButton");
        RejectButton.IsDefault = true;
    }

    private void Reject_Click(object sender, RoutedEventArgs e) => Finish(HostKeyDecision.Reject);

    private void AcceptOnce_Click(object sender, RoutedEventArgs e) => Finish(HostKeyDecision.AcceptOnce);

    private void AcceptAndStore_Click(object sender, RoutedEventArgs e) => Finish(HostKeyDecision.AcceptAndStore);

    private void Finish(HostKeyDecision decision)
    {
        _decision = decision;
        Close();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
