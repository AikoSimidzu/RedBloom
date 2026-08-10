using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Views;

/// <summary>
/// The AI page: the agents this install can run, and how each one reaches a model.
/// </summary>
/// <remarks>
/// Kept apart from the appearance page because it saves on different terms. Appearance settings
/// apply as they are typed; a half-typed endpoint or a partial key applies to nothing, so this
/// page writes the settings file but never tries to act on a field mid-edit.
/// </remarks>
public partial class AiSettingsPage : UserControl
{
    private readonly AppSettings _settings = ThemeService.Settings;
    private readonly ObservableCollection<AiAgent> _agents;

    /// <summary>Suppresses the field handlers while the editor is being filled from an agent.</summary>
    private bool _loading;

    private CancellationTokenSource? _test;

    /// <summary>Raised when the user asks to run an agent, so the window can open a tab for it.</summary>
    public static event Action<AiAgent>? LaunchRequested;

    public AiSettingsPage()
    {
        InitializeComponent();

        _agents = [.. _settings.Agents];
        AgentList.ItemsSource = _agents;

        if (_agents.Count > 0)
        {
            AgentList.SelectedIndex = 0;
        }

        RefreshEditorState();

        LocalizationService.Changed += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            LocalizationService.Changed -= OnLanguageChanged;
            _test?.Cancel();
        };
    }

    private AiAgent? Selected => AgentList.SelectedItem as AiAgent;

    private void OnLanguageChanged() => RefreshHints();

    // ---- list ----

    private void AddAgent_Click(object sender, RoutedEventArgs e)
    {
        var agent = new AiAgent { Name = LocalizationService.T("L_AiNewAgent") };
        _agents.Add(agent);
        AgentList.SelectedItem = agent;
        Persist();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void DeleteAgent_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } agent)
        {
            return;
        }

        var index = _agents.IndexOf(agent);
        _agents.Remove(agent);
        Persist();

        // Land on a neighbour rather than on nothing, so the editor stays useful.
        if (_agents.Count > 0)
        {
            AgentList.SelectedIndex = Math.Clamp(index, 0, _agents.Count - 1);
        }
        else
        {
            RefreshEditorState();
        }
    }

    /// <summary>Takes a config from another tool and turns it into agents.</summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfigImportDialog { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() != true || dialog.Agents.Count == 0)
        {
            return;
        }

        foreach (var agent in dialog.Agents)
        {
            _agents.Add(agent);
        }

        AgentList.SelectedItem = dialog.Agents[0];
        Persist();

        // Selecting the agent above cleared this, so the outcome is reported afterwards.
        TestResult.Text = string.Format(
            CultureInfo.CurrentCulture, LocalizationService.T("L_AiImported"), dialog.Agents.Count);
    }

    private void Agent_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        RefreshEditorState();

        if (Selected is not { } agent)
        {
            return;
        }

        _loading = true;
        try
        {
            NameBox.Text = agent.Name;
            ProviderBox.SelectedIndex = agent.Provider == AiProvider.Anthropic ? 0 : 1;
            BaseUrlBox.Text = agent.BaseUrl;
            ApiKeyBox.Password = agent.ApiKey ?? string.Empty;
            ModelBox.Text = agent.Model;
            AvatarBox.Text = agent.AvatarPath;
            NameColorBox.Text = string.IsNullOrWhiteSpace(agent.NameColor) ? _settings.Accent : agent.NameColor;
            MaxTokensBox.Text = agent.MaxTokens.ToString(CultureInfo.InvariantCulture);
            ContextWindowBox.Text = agent.ContextWindow.ToString(CultureInfo.InvariantCulture);
            EffortBox.SelectedIndex = EffortIndex(agent.Effort);
            ThinkingBox.IsChecked = agent.Thinking;
            AllowCommandsBox.IsChecked = agent.AllowCommands;
            AskBeforeRunBox.IsChecked = agent.AskBeforeRun;
            AllowedCommandsBox.Text = string.Join(Environment.NewLine, agent.AllowedCommands);
            SystemPromptBox.Text = agent.SystemPrompt;
        }
        finally
        {
            _loading = false;
        }

        TestResult.Text = string.Empty;
        ShowAvatar(Selected.AvatarPath);
        RefreshHints();
    }

    // ---- editing ----

    private void Field_Changed(object sender, RoutedEventArgs e) => Capture();

    private void ApiKey_Changed(object sender, RoutedEventArgs e) => Capture();

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        Capture();
        RefreshHints();
    }

    private void Commands_Changed(object sender, RoutedEventArgs e)
    {
        Capture();
        RefreshHints();
    }

    private void BrowseAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AvatarBox.Text = dialog.FileName;
        }
    }

    private void ClearAvatar_Click(object sender, RoutedEventArgs e) => AvatarBox.Text = string.Empty;

    /// <summary>Opens the shared colour picker under the nick swatch.</summary>
    private void NameColorSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Controls.ColorPickerPopup.Show((UIElement)sender, NameColorBox.Text, hex => NameColorBox.Text = hex);

    /// <summary>
    /// Draws the chosen picture beside the field.
    /// </summary>
    /// <remarks>
    /// Loaded on demand rather than held open, so the file can still be replaced or deleted
    /// while RedBloom is running — the same reason the background pictures are loaded this way.
    /// </remarks>
    private void ShowAvatar(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AvatarPreview.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 60;
            bitmap.EndInit();
            bitmap.Freeze();

            AvatarPreview.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException
                                       or ArgumentException or InvalidOperationException)
        {
            AvatarPreview.Source = null;
        }
    }

    /// <summary>Copies the editor back into the selected agent and saves.</summary>
    private void Capture()
    {
        if (_loading || Selected is not { } agent)
        {
            return;
        }

        agent.Name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? LocalizationService.T("L_AiNewAgent")
            : NameBox.Text.Trim();

        agent.Provider = ProviderBox.SelectedIndex == 1 ? AiProvider.OpenAiCompatible : AiProvider.Anthropic;
        agent.BaseUrl = BaseUrlBox.Text.Trim();
        agent.ApiKey = ApiKeyBox.Password;
        agent.Model = ModelBox.Text.Trim();
        agent.AvatarPath = AvatarBox.Text.Trim();
        agent.NameColor = NameColorBox.Text.Trim();
        ShowAvatar(agent.AvatarPath);
        agent.SystemPrompt = SystemPromptBox.Text;
        agent.Thinking = ThinkingBox.IsChecked == true;
        agent.AllowCommands = AllowCommandsBox.IsChecked == true;
        agent.AskBeforeRun = AskBeforeRunBox.IsChecked == true;

        // One pattern per line, blanks dropped — the list is edited as text because that is how
        // it is read, and because entries arrive here one at a time from the approval prompt.
        agent.AllowedCommands =
        [
            .. AllowedCommandsBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0),
        ];

        if (EffortBox.SelectedItem is ComboBoxItem { Content: string effort })
        {
            agent.Effort = effort;
        }

        // A field mid-edit is not an error worth shouting about: keep the last good number and
        // let the clamp in the model decide what is sane.
        if (int.TryParse(MaxTokensBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens))
        {
            agent.MaxTokens = maxTokens;
        }

        if (int.TryParse(ContextWindowBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window))
        {
            agent.ContextWindow = window;
        }

        Persist();
    }

    private void Persist()
    {
        _settings.Agents = [.. _agents];
        ThemeService.Save();

        // The list shows the name and model, and neither raises a collection change on its own.
        AgentList.Items.Refresh();
    }

    // ---- state ----

    private void RefreshEditorState()
    {
        var has = Selected is not null;
        Editor.IsEnabled = has;
        DeleteAgentButton.IsEnabled = has;
    }

    /// <summary>
    /// Shows what the blank fields fall back to, and hides the settings that do not apply.
    /// </summary>
    private void RefreshHints()
    {
        if (Selected is not { } agent)
        {
            return;
        }

        BaseUrlHint.Text = string.Format(
            CultureInfo.CurrentCulture, LocalizationService.T("L_AiBaseUrlNote"), agent.ResolvedBaseUrl);

        // Effort and thinking are Anthropic's; the chat-completions shape has no equivalent, so
        // they are hidden rather than shown as settings that quietly do nothing.
        var anthropic = agent.Provider == AiProvider.Anthropic;
        var visibility = anthropic ? Visibility.Visible : Visibility.Collapsed;
        EffortRow.Visibility = visibility;
        ThinkingBox.Visibility = visibility;
        ThinkingHint.Visibility = visibility;

        // Asking before each command, and the standing allowances, only mean something once
        // commands are allowed at all.
        AskBeforeRunBox.IsEnabled = agent.AllowCommands;

        var commands = agent.AllowCommands ? Visibility.Visible : Visibility.Collapsed;
        AllowedLabel.Visibility = commands;
        AllowedCommandsBox.Visibility = commands;
        AllowedHint.Visibility = commands;
    }

    private static int EffortIndex(string? effort) => effort?.ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        "xhigh" => 3,
        "max" => 4,
        _ => 2,
    };

    // ---- actions ----

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } agent)
        {
            return;
        }

        _test?.Cancel();
        _test?.Dispose();
        var test = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _test = test;

        TestButton.IsEnabled = false;
        TestResult.Text = LocalizationService.T("L_AiTesting");

        try
        {
            // Against a copy: the live agent can be edited while the request is in flight.
            using var transport = AgentTransports.For(agent.Clone());
            var failure = await transport.TestAsync(test.Token).ConfigureAwait(true);

            TestResult.Text = failure is null
                ? LocalizationService.T("L_AiTestOk")
                : string.Format(CultureInfo.CurrentCulture, LocalizationService.T("L_AiTestFailed"), failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TestResult.Text = string.Format(
                CultureInfo.CurrentCulture, LocalizationService.T("L_AiTestFailed"), ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } agent)
        {
            // A snapshot, so later edits on this page cannot change a session already running.
            LaunchRequested?.Invoke(agent.Clone());
        }
    }
}
