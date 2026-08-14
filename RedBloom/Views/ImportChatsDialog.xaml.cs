using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Views;

/// <summary>Picks which Claude Code sessions to bring into RedBloom.</summary>
public partial class ImportChatsDialog : Window
{
    private readonly ObservableCollection<Row> _rows = [];
    private readonly CancellationTokenSource _cancel = new();

    private ImportChatsDialog()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>Scans the sessions off the UI thread, driving the progress bar, then fills the list.</summary>
    private async Task ScanAsync()
    {
        SetBusy(true);
        Bar.Value = 0;
        BusyLabel.Text = Fmt("L_ImportScanning", 0, 0);

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Bar.Maximum = Math.Max(1, p.Total);
            Bar.Value = p.Done;
            BusyLabel.Text = Fmt("L_ImportScanning", p.Done, p.Total);
        });

        IReadOnlyList<ImportedChat> found;
        try
        {
            found = await ClaudeImport.DiscoverAsync(progress, _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _rows.Clear();
        foreach (var chat in found)
        {
            _rows.Add(new Row(chat));
        }

        SetBusy(false);
        Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = _rows.Count > 0;
        CountLabel.Text = string.Format(CultureInfo.CurrentCulture, LocalizationService.T("L_ImportFound"), _rows.Count);
    }

    /// <summary>Swaps the list and its controls for the progress panel, or back.</summary>
    private void SetBusy(bool busy)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HeaderRow.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ListBorder.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

        if (busy)
        {
            Empty.Visibility = Visibility.Collapsed;
        }

        ImportButton.IsEnabled = !busy && _rows.Count > 0;
    }

    private static string Fmt(string key, int done, int total) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.T(key), done, total);

    /// <summary>Shows the dialog and imports the chosen chats; returns how many were newly imported.</summary>
    public static int Run(Window owner)
    {
        var dialog = new ImportChatsDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Imported : 0;
    }

    private int Imported { get; set; }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var on = SelectAll.IsChecked == true;

        foreach (var row in _rows)
        {
            row.Chosen = on;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Chosen).Select(r => r.Chat).ToList();

        if (chosen.Count == 0)
        {
            DialogResult = false;
            return;
        }

        SetBusy(true);
        Bar.Maximum = chosen.Count;
        Bar.Value = 0;
        BusyLabel.Text = Fmt("L_ImportImporting", 0, chosen.Count);

        var done = 0;
        foreach (var chat in chosen)
        {
            // The save touches the shared chat list, so it stays on the UI thread; yielding between
            // chats lets the bar repaint rather than jumping straight to full.
            if (ClaudeImport.Import(chat))
            {
                Imported++;
            }

            done++;
            Bar.Value = done;
            BusyLabel.Text = Fmt("L_ImportImporting", done, chosen.Count);
            await Task.Yield();
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancel.Cancel();
        DialogResult = false;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private sealed class Row(ImportedChat chat) : INotifyPropertyChanged
    {
        private bool _chosen = true;

        public ImportedChat Chat { get; } = chat;

        public string Title => Chat.Title;

        public string Detail
        {
            get
            {
                var when = Chat.Updated.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
                var count = string.Format(CultureInfo.CurrentCulture,
                    Services.LocalizationService.T("L_ImportMessages"), Chat.Messages);
                return Chat.Cwd.Length > 0 ? $"{when} · {count} · {Chat.Cwd}" : $"{when} · {count}";
            }
        }

        public bool Chosen
        {
            get => _chosen;
            set
            {
                _chosen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chosen)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
