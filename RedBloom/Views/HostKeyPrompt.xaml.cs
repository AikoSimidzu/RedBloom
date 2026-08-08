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
            ? string.Format(LocalizationService.T("L_HkAlgoBits"), key.Algorithm, key.KeyLength)
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
        HeaderText.Text = LocalizationService.T("L_HkUnknownServer");
        BodyText.Text = string.Format(LocalizationService.T("L_HkFirstBody"), EndpointText.Text);
        HintText.Text = LocalizationService.T("L_HkFirstHint");
        AcceptStoreButton.IsDefault = true;
    }

    private void ConfigureForNewAlgorithm()
    {
        HeaderIcon.Text = "";
        HeaderText.Text = LocalizationService.T("L_HkNewKeyType");
        BodyText.Text = string.Format(LocalizationService.T("L_HkNewAlgoBody"), EndpointText.Text);
        HintText.Text = LocalizationService.T("L_HkNewAlgoHint");
        AcceptStoreButton.IsDefault = true;
    }

    private void ConfigureForChangedKey(KnownHost? stored)
    {
        HeaderIcon.Text = "";
        HeaderIcon.Foreground = (Brush)FindResource("Danger");
        HeaderText.Text = LocalizationService.T("L_HkChanged");
        HeaderText.Foreground = (Brush)FindResource("Danger");
        RootBorder.BorderBrush = (Brush)FindResource("Danger");

        BodyText.Text = string.Format(LocalizationService.T("L_HkChangedBody"), EndpointText.Text);
        HintText.Text = LocalizationService.T("L_HkChangedHint");

        if (stored is not null)
        {
            StoredPanel.Visibility = Visibility.Visible;
            StoredFingerprintText.Text = $"SHA256:{stored.Sha256Fingerprint}";
        }

        // One deliberate choice only: no "just this once" escape hatch for a changed key.
        AcceptOnceButton.Visibility = Visibility.Collapsed;
        AcceptStoreButton.Content = LocalizationService.T("L_ReplaceStoredKey");
        AcceptStoreButton.Style = (Style)FindResource("GhostButton");
        RejectButton.Content = LocalizationService.T("L_CancelConnection");
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
