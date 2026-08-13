using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using RedBloom.Services.Ai;

namespace RedBloom.Views;

/// <summary>Picks which Claude Code sessions to bring into RedBloom.</summary>
public partial class ImportChatsDialog : Window
{
    private readonly List<Row> _rows;

    private ImportChatsDialog()
    {
        InitializeComponent();

        _rows = [.. ClaudeImport.Discover().Select(c => new Row(c))];
        List.ItemsSource = _rows;

        Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = _rows.Count > 0;
        CountLabel.Text = string.Format(
            CultureInfo.CurrentCulture, Services.LocalizationService.T("L_ImportFound"), _rows.Count);
    }

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

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        Imported = _rows.Where(r => r.Chosen).Count(r => ClaudeImport.Import(r.Chat));
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
