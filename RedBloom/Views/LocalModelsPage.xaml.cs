using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Views;

/// <summary>Paints a verdict in the colour that says how good it is.</summary>
public sealed class FitBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush(value is Fit fit
            ? fit switch
            {
                Fit.Good => Color.FromRgb(0x6F, 0xC2, 0x76),
                Fit.Tight => Color.FromRgb(0xD8, 0xC0, 0x5A),
                Fit.Heavy => Color.FromRgb(0xD8, 0x8F, 0x4A),
                _ => Color.FromRgb(0xC5, 0x5F, 0x5F),
            }
            : Colors.Gray);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>One downloadable file as the list shows it.</summary>
public sealed record FileRow(HubFile File)
{
    /// <summary>The quantisation, or the file's own name when it does not declare one.</summary>
    public string Quant => File.Quantisation.Length > 0
        ? File.Quantisation
        : System.IO.Path.GetFileNameWithoutExtension(File.Name);

    public string Size => string.Format(
        CultureInfo.CurrentCulture, "{0:0.#} GB", MachineFit.Gigabytes(File.Bytes))
        .Replace("GB", LocalizationService.T("L_UnitGb"), StringComparison.Ordinal);

    public string Parts => File.IsSplit
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.T("L_LocalParts"), File.Parts)
        : string.Empty;

    public string Verdict => MachineFit.Describe(File.Fit);

    /// <summary>
    /// Why this one is rated as it is — but only when the rating needs explaining.
    /// </summary>
    /// <remarks>
    /// A list where every row says the same sentence about the same machine is a wall of text
    /// that stops being read by the third line. The ones that will run well need no argument;
    /// the badge already says so. The caveats are what someone is scanning for.
    /// </remarks>
    public string Why => File.Fit == Fit.Good
        ? string.Empty
        : MachineFit.Explain(File.Fit, File.Bytes);

    public Fit Fit => File.Fit;
}

/// <summary>One hub result as the list shows it.</summary>
public sealed record ModelRow(HubModel Model)
{
    public string Name => Model.Name;

    public string By => Model.Author + " · " + Compact(Model.Downloads);

    /// <summary>
    /// A download count short enough to sit under a name.
    /// </summary>
    /// <remarks>
    /// "6,2M" rather than "6 246 657": the figure is there to say how well used a model is, and
    /// at that job the exact number is only in the way.
    /// </remarks>
    private static string Compact(long count) => count switch
    {
        >= 1_000_000 => string.Format(CultureInfo.CurrentCulture,
            LocalizationService.T("L_UnitMillions"), Math.Round(count / 1_000_000d, 1)),
        >= 1_000 => string.Format(CultureInfo.CurrentCulture,
            LocalizationService.T("L_UnitThousands"), Math.Round(count / 1_000d, 0)),
        _ => count.ToString(CultureInfo.CurrentCulture),
    };
}

/// <summary>
/// One model on this machine, whether it came from a file or from the engine's own store.
/// </summary>
/// <param name="Model">The name the engine answers to, which renaming never changes.</param>
/// <param name="Path">Where its file is, or null when only the engine holds it.</param>
public sealed record LocalRow(string Model, string Name, string Detail, string? Path)
{
    public bool HasFile => Path is not null;
}

/// <summary>
/// Models that run on this machine: what is installed, what is listening, and what could be
/// fetched from the hub.
/// </summary>
/// <remarks>
/// The page does no inference of its own — see <see cref="LocalRunner"/> for why. Its job is to
/// answer the question that actually stops people running models locally, which is not "how do I
/// download one" but "which of these will my computer survive".
/// </remarks>
public partial class LocalModelsPage : UserControl
{
    /// <summary>
    /// How often the download card is redrawn.
    /// </summary>
    /// <remarks>
    /// Progress arrives once per megabyte, which on a fast line is dozens of times a second —
    /// far more than anyone can read, and enough repainting to be felt. Four times a second is
    /// smooth to the eye and cheap.
    /// </remarks>
    private static readonly TimeSpan Redraw = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _work;
    private bool _cancelled;

    /// <summary>The chosen model's files, kept so the filter can be flipped without asking again.</summary>
    private IReadOnlyList<HubFile> _files = [];

    /// <summary>When the current download started, and where it had got to at the last redraw.</summary>
    private DateTime _started;
    private DateTime _lastDrawn;
    private long _lastDone;
    private double _rate;

    private Storyboard? _shine;

    public LocalModelsPage()
    {
        InitializeComponent();

        PaintProgressBar();

        // Named before the first refresh answers, so the button is never a blank box while the
        // engine is being looked for.
        UpdateEngineButton(running: false);

        ThemeService.Applied += PaintProgressBar;
        Unloaded += (_, _) => ThemeService.Applied -= PaintProgressBar;

        SearchBox.Text = "qwen";
        Loaded += async (_, _) =>
        {
            await RefreshAsync().ConfigureAwait(true);
            await SearchAsync().ConfigureAwait(true);
        };
    }

    /// <summary>Colours the filled part from the theme's accent.</summary>
    private void PaintProgressBar()
    {
        var settings = ThemeService.Settings;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            [
                new GradientStop(ThemeService.ParseColor(settings.AccentDim, Colors.DarkRed), 0),
                new GradientStop(ThemeService.ParseColor(settings.Accent, Colors.Red), 1),
            ],
        };

        gradient.Freeze();
        FillBar.Background = gradient;
    }

    /// <summary>Starts the sweeping highlight, and the timing the rate is worked out from.</summary>
    private void BeginProgress(string what)
    {
        _cancelled = false;
        _started = DateTime.UtcNow;
        _lastDrawn = DateTime.MinValue;
        _lastDone = 0;
        _rate = 0;

        ProgressName.Text = what;
        ProgressPercent.Text = "0%";
        ProgressText.Text = string.Empty;
        ProgressRate.Text = string.Empty;
        Fill.Width = 0;
        ProgressRow.Visibility = Visibility.Visible;

        // Laid out now so the track has a width to measure the sweep against; it was collapsed
        // a moment ago and would otherwise still be zero.
        ProgressRow.UpdateLayout();

        var span = Track.ActualWidth > 0 ? Track.ActualWidth : 600;

        var sweep = new DoubleAnimation
        {
            From = -Shine.Width,
            To = span,
            Duration = TimeSpan.FromSeconds(1.4),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(sweep, ShineShift);
        Storyboard.SetTargetProperty(sweep, new PropertyPath(TranslateTransform.XProperty));

        _shine = new Storyboard();
        _shine.Children.Add(sweep);
        _shine.Begin();
    }

    private void EndProgress()
    {
        _shine?.Stop();
        _shine = null;
        ProgressRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Draws one report: how far along, how fast, and how much longer.
    /// </summary>
    /// <remarks>
    /// The rate is smoothed rather than measured over the last interval alone. A raw figure
    /// jumps between wildly different numbers as buffers fill and empty, and a remaining time
    /// computed from it is unreadable — it swings by minutes between one second and the next.
    /// </remarks>
    private void Report(long done, long total)
    {
        var now = DateTime.UtcNow;

        if (now - _lastDrawn < Redraw && done < total)
        {
            return;
        }

        if (_lastDrawn != DateTime.MinValue)
        {
            var seconds = (now - _lastDrawn).TotalSeconds;

            if (seconds > 0)
            {
                var latest = (done - _lastDone) / seconds;
                _rate = _rate > 0 ? (_rate * 0.7) + (latest * 0.3) : latest;
            }
        }

        _lastDrawn = now;
        _lastDone = done;

        var share = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;

        Fill.Width = Track.ActualWidth * share;
        ProgressPercent.Text = share.ToString("P0", CultureInfo.CurrentCulture);

        ProgressText.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.T("L_DlDone"),
            MachineFit.Gigabytes(done),
            MachineFit.Gigabytes(total));

        var parts = new List<string>();

        if (_rate > 0)
        {
            parts.Add(string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.T("L_DlSpeed"),
                Math.Round(_rate / 1024 / 1024, 1)));

            if (total > done)
            {
                var left = TimeSpan.FromSeconds((total - done) / _rate);

                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationService.T("L_DlLeft"),
                    left.TotalHours >= 1
                        ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
                        : $"{(int)left.TotalMinutes}:{left.Seconds:00}"));
            }
        }

        ProgressRate.Text = string.Join("   ", parts);
    }

    private void Track_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The bar is measured in pixels, so a resized window has to redraw it at the same share.
        if (Track.ActualWidth > 0 && e.PreviousSize.Width > 0)
        {
            Fill.Width *= Track.ActualWidth / e.PreviousSize.Width;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        _work?.Cancel();
    }

    /// <summary>Raised when a local model should be opened as an agent.</summary>
    public static event Action<AiAgent>? LaunchRequested;

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync().ConfigureAwait(true);

    private async void Search_Click(object sender, RoutedEventArgs e) =>
        await SearchAsync().ConfigureAwait(true);

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshAsync()
    {
        MachineLine.Text = MachineFit.Summary;

        var runners = await LocalRunner.DetectAsync().ConfigureAwait(true);
        var running = runners.Where(r => r.Running).ToList();

        ShowInstalled(running);

        UpdateEngineButton(running.Count > 0);

        if (running.Count == 0)
        {
            RunnerLine.Text = LocalizationService.T(
                OllamaEngine.IsInstalled || OllamaEngine.SystemCopy is not null
                    ? "L_LocalEngineIdle"
                    : "L_LocalNoRunner");

            return;
        }

        RunnerLine.Text = string.Join("   ", running.Select(r =>
            $"{r.Name}: {(r.Models.Count == 0
                ? LocalizationService.T("L_LocalNoModelLoaded")
                : string.Join(", ", r.Models))}"));

        // A runner with something loaded is the whole point, so the way to use it is offered
        // right there rather than left to be assembled by hand in the agent settings.
        UseButtons(running);
    }

    /// <summary>
    /// Everything on this machine: the files that were downloaded, and whatever the engine
    /// already holds.
    /// </summary>
    /// <remarks>
    /// Both are listed because both can be chosen as an agent, and anything that can be chosen
    /// should be nameable. A model the engine holds without a file of its own — pulled with
    /// Ollama directly, say — has nothing to start or delete here, so those buttons stay away
    /// rather than sitting there greyed out.
    /// </remarks>
    private void ShowInstalled(IReadOnlyList<RunnerState> running)
    {
        var rows = new List<LocalRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in LocalRunner.Downloaded())
        {
            var model = System.IO.Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
            seen.Add(LocalAgents.Key(model));

            rows.Add(new LocalRow(
                model,
                LocalAgents.NameOf(model),
                $"{MachineFit.Gigabytes(file.Length)} {LocalizationService.T("L_UnitGb")} · "
                    + MachineFit.Describe(MachineFit.Rate(file.Length)),
                file.FullName));
        }

        foreach (var model in running.SelectMany(runner => runner.Models))
        {
            if (seen.Add(LocalAgents.Key(model)))
            {
                rows.Add(new LocalRow(
                    model,
                    LocalAgents.NameOf(model),
                    LocalizationService.T("L_LocalInEngine"),
                    null));
            }
        }

        DownloadedList.ItemsSource = rows;
        DownloadedBox.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RenameModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LocalRow row })
        {
            return;
        }

        var dialog = new TextPromptDialog(
            LocalizationService.T("L_LocalRenameTitle"),
            LocalizationService.T("L_LocalRenameNote"),
            row.Name)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LocalAgents.Rename(row.Model, dialog.Answer);

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The one button that moves the engine along, whichever stage it is at.
    /// </summary>
    /// <remarks>
    /// One button rather than three, because at any moment only one of install, start and stop
    /// is the thing to do, and a row of greyed-out buttons explains less than a single label
    /// that says what happens next.
    /// </remarks>
    private void UpdateEngineButton(bool running)
    {
        EngineButton.Content = LocalizationService.T(
            running ? "L_LocalEngineStop"
            : OllamaEngine.IsInstalled || OllamaEngine.SystemCopy is not null ? "L_LocalEngineStart"
            : "L_LocalEngineInstall");

        EngineButton.Tag = running ? "stop"
            : OllamaEngine.IsInstalled || OllamaEngine.SystemCopy is not null ? "start"
            : "install";
    }

    private async void Engine_Click(object sender, RoutedEventArgs e)
    {
        var step = EngineButton.Tag as string;

        if (step == "stop")
        {
            OllamaEngine.Stop();
            LocalRunner.Stop();
            await RefreshAsync().ConfigureAwait(true);

            return;
        }

        EngineButton.IsEnabled = false;

        try
        {
            if (step == "install" && await InstallEngineAsync().ConfigureAwait(true) is false)
            {
                return;
            }

            RunnerLine.Text = LocalizationService.T("L_LocalEngineStarting");

            if (await OllamaEngine.ServeAsync().ConfigureAwait(true) is { } refused)
            {
                MessageBox.Show(refused, LocalizationService.T("L_LocalModels"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            EngineButton.IsEnabled = true;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Asks first, because this is well over a gigabyte.</summary>
    private async Task<bool> InstallEngineAsync()
    {
        var asked = MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture,
                LocalizationService.T("L_LocalEngineAsk"), OllamaEngine.Folder),
            LocalizationService.T("L_LocalModels"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (asked != MessageBoxResult.Yes)
        {
            return false;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        BeginProgress(LocalizationService.T("L_LocalEngineInstall"));

        var progress = new Progress<(long Done, long Total)>(report => Report(report.Done, report.Total));

        RunnerLine.Text = LocalizationService.T("L_LocalEngineFetching");

        var refused = await OllamaEngine.InstallAsync(progress, _work.Token).ConfigureAwait(true);

        EndProgress();

        if (refused is not null && !_cancelled)
        {
            MessageBox.Show(refused, LocalizationService.T("L_LocalModels"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return refused is null;
    }

    /// <summary>Rebuilds the row of "use this one" buttons under the runner line.</summary>
    private void UseButtons(IReadOnlyList<RunnerState> running)
    {
        var offers = new List<AiAgent>();

        foreach (var runner in running)
        {
            foreach (var model in runner.Models)
            {
                offers.Add(AgentFor(runner, model));
            }
        }

        if (offers.Count == 0)
        {
            return;
        }

        // Shown as a note plus one button each; there are rarely more than a handful.
        var panel = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };

        foreach (var agent in offers)
        {
            var button = new Button
            {
                Content = $"{LocalizationService.T("L_LocalUse")} {agent.Model}",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(11, 4, 11, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = agent,
            };

            button.Click += Use_Click;
            panel.Children.Add(button);
        }

        if (RunnerLine.Parent is StackPanel host)
        {
            // Replace whatever was there last time rather than stacking a new row on each refresh.
            for (var i = host.Children.Count - 1; i >= 0; i--)
            {
                if (host.Children[i] is WrapPanel)
                {
                    host.Children.RemoveAt(i);
                }
            }

            host.Children.Add(panel);
        }
    }

    /// <summary>
    /// The agent for a model the engine is serving.
    /// </summary>
    /// <remarks>
    /// Built by <see cref="LocalAgents"/> rather than here, so it carries the same id as the
    /// entry in the sidebar. Made locally it got a fresh identity each time, and chats — which
    /// are filed under the agent's id — ended up scattered across ids that nothing listed
    /// afterwards. They were being saved; there was simply no longer an agent claiming them.
    /// </remarks>
    private static AiAgent AgentFor(RunnerState runner, string model) =>
        LocalAgents.For(model, runner.BaseUrl);

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AiAgent agent })
        {
            LaunchRequested?.Invoke(agent.Clone());
        }
    }

    private async Task SearchAsync()
    {
        FilesHeader.Text = LocalizationService.T("L_LocalSearching");
        FileList.ItemsSource = null;

        var models = await HuggingFace.SearchAsync(SearchBox.Text).ConfigureAwait(true);

        ModelList.ItemsSource = models.Select(model => new ModelRow(model)).ToList();
        FilesHeader.Text = models.Count == 0
            ? LocalizationService.T("L_LocalNothing")
            : LocalizationService.T("L_LocalPick");
    }

    private async void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow { Model: var model })
        {
            return;
        }

        FilesHeader.Text = model.Id;
        FileList.ItemsSource = null;

        _files = await HuggingFace.FilesAsync(model.Id).ConfigureAwait(true);
        ShowFiles();

        if (_files.Count == 0)
        {
            FilesHeader.Text = $"{model.Id} — {LocalizationService.T("L_LocalNoFiles")}";
        }
    }

    /// <summary>
    /// Fills the file list, best fit first.
    /// </summary>
    /// <remarks>
    /// Ordered by what the machine can do with it rather than by size alone, because that is the
    /// question being asked. Within a verdict the biggest comes first: of the ones that run well,
    /// the largest is the one worth having.
    /// </remarks>
    private void ShowFiles()
    {
        var shown = OnlyFitting.IsChecked == true
            ? _files.Where(file => file.Fit != Fit.No)
            : _files;

        FileList.ItemsSource = shown
            .OrderBy(file => file.Fit)
            .ThenByDescending(file => file.Bytes)
            .Select(file => new FileRow(file))
            .ToList();
    }

    private void OnlyFitting_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ShowFiles();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FileRow row })
        {
            return;
        }

        if (row.File.IsSplit)
        {
            // Only the first part would come down, and a partial model is worse than none: it
            // looks installed and fails at load.
            MessageBox.Show(
                LocalizationService.T("L_LocalSplitNote"),
                LocalizationService.T("L_LocalModels"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        BeginProgress(row.File.Name);

        var progress = new Progress<(long Done, long Total)>(report => Report(report.Done, report.Total));

        var landed = await HuggingFace
            .DownloadAsync(row.File, LocalRunner.ModelsFolder, progress, _work.Token)
            .ConfigureAwait(true);

        EndProgress();

        if (landed is null && !_cancelled)
        {
            MessageBox.Show(
                LocalizationService.T("L_LocalFailed"),
                LocalizationService.T("L_LocalModels"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        RunnerLine.Text = LocalizationService.T("L_LocalStarting");

        // Ollama first when it is there: it keeps the model in its own store, so it stays
        // available afterwards instead of only while a server this app started is alive.
        var refused = await OllamaEngine.AnsweringAsync().ConfigureAwait(true)
            ? await OllamaEngine.ImportAsync(path).ConfigureAwait(true)
            : await LocalRunner.StartAsync(path).ConfigureAwait(true);

        if (refused is not null)
        {
            MessageBox.Show(refused, LocalizationService.T("L_LocalModels"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LocalRow row })
        {
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture, LocalizationService.T("L_LocalDeleteAsk"), row.Name),
            LocalizationService.T("L_LocalModels"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        // The file, when there is one. On its own this was the whole of deletion, and the reason
        // some models "came back": once imported, the engine keeps serving them from its store, so
        // discovery found them again after their file was gone.
        if (row.Path is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, LocalizationService.T("L_LocalModels"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // The store copy, so a model imported into the engine actually leaves the list. Harmless
        // when the engine never held it.
        if (await OllamaEngine.RemoveAsync(row.Model).ConfigureAwait(true) is { } complaint)
        {
            MessageBox.Show(complaint, LocalizationService.T("L_LocalModels"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The name given to it, so a model of the same name downloaded later starts fresh rather
        // than inheriting a label from one that is gone.
        LocalAgents.Rename(row.Model, string.Empty);

        await RefreshAsync().ConfigureAwait(true);
    }
}
