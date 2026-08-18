using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Controls;

/// <summary>
/// A project's home: its name and description, its relationship tree shown inline (and expandable to
/// a full tab), and a Markdown info file for everything else — edited with the same file editor used
/// everywhere, so it reads and renders the same way opening any .md file does. The chats and rooms
/// live in the sidebar tree under the project, not here.
/// </summary>
public partial class ProjectHomeView : UserControl
{
    private readonly Project _project;
    private ProjectGraphView? _graph;
    private FileEditorView? _info;
    private bool _loading = true;

    public ProjectHomeView(Project project)
    {
        _project = project;
        InitializeComponent();

        NameBox.Text = project.Name;
        DescriptionBox.Text = project.Description;
        FolderText.Text = project.Folder;

        _loading = false;
        Loaded += OnLoaded;
    }

    /// <summary>Raised when a chat linked on the graph is opened.</summary>
    public event Action<ChatSession>? ChatActivated;

    /// <summary>Raised when a room linked on the graph is opened.</summary>
    public event Action<ChatRoom>? RoomActivated;

    /// <summary>Raised when a file is opened — a graph node, or the info file expanded to a full tab.</summary>
    public event Action<string>? FileActivated;

    /// <summary>Raised by the "expand" button — the window opens the project's graph as a full tab.</summary>
    public event Action<Project>? GraphRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // The relationship tree is shown inline, right here — visible the moment the project opens,
        // with the expand button for the full-tab view. The same graph is edited either way.
        _graph = new ProjectGraphView(_project);
        _graph.OpenRequested += OnGraphOpen;
        GraphHost.Content = _graph;

        // The info file is edited with the shared file editor, pointed at PROJECT.md in the folder.
        EnsureInfoFile();
        _info = new FileEditorView(InfoPath);
        InfoHost.Content = _info;

        RefreshStats();

        // Coming back to this tab after editing the tree in the full-tab view: catch the inline copy up.
        IsVisibleChanged += OnVisibleChanged;
        Unloaded += (_, _) =>
        {
            IsVisibleChanged -= OnVisibleChanged;
            _info?.Dispose();
            _graph?.Dispose();
        };
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _graph?.Reload();
            RefreshStats();
        }
    }

    // ---- activity monitoring ----

    private async void RefreshStats()
    {
        ProjectStats.Stats stats;
        try
        {
            stats = await ProjectStats.ComputeAsync(_project).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        StatsPanel.Children.Clear();

        StatsPanel.Children.Add(Tile(
            stats.Chats.ToString(),
            $"{LocalizationService.T("L_StatChats")} · {stats.ChatMessages}"));

        StatsPanel.Children.Add(Tile(
            stats.Rooms.ToString(),
            $"{LocalizationService.T("L_StatRooms")} · {stats.RoomMessages}"));

        StatsPanel.Children.Add(Tile(
            stats.Files.ToString(),
            $"{LocalizationService.T("L_StatFiles")} · {HumanSize(stats.Bytes)}"));

        if (stats.Git is { } git)
        {
            var changes = git.Changes == 0
                ? LocalizationService.T("L_StatClean")
                : string.Format(LocalizationService.T("L_StatChanges"), git.Changes);
            var tile = Tile(git.Branch, changes);
            if (git.LastCommit.Length > 0)
            {
                tile.ToolTip = git.LastCommit;
            }

            StatsPanel.Children.Add(tile);
        }

        StatsPanel.Children.Add(Tile(
            stats.LastActivity is { } when ? Relative(when) : "—",
            LocalizationService.T("L_StatLastActivity")));
    }

    /// <summary>One stat card: a big value over a small label.</summary>
    private Border Tile(string value, string label)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFaint"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"),
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Surface"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Divider"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 9, 15, 9),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 96,
            Child = stack,
        };
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    private static string Relative(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span < TimeSpan.FromMinutes(1)) return LocalizationService.T("L_StatJustNow");
        if (span < TimeSpan.FromHours(1)) return string.Format(LocalizationService.T("L_StatMinutesAgo"), (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return string.Format(LocalizationService.T("L_StatHoursAgo"), (int)span.TotalHours);
        if (span < TimeSpan.FromDays(30)) return string.Format(LocalizationService.T("L_StatDaysAgo"), (int)span.TotalDays);
        return when.ToString("d MMM yyyy");
    }

    private void OnGraphOpen(string kind, string refId)
    {
        switch (kind)
        {
            case "chat" when ChatStore.Chats.FirstOrDefault(c => c.Id == refId) is { } chat:
                ChatActivated?.Invoke(chat);
                break;
            case "room" when RoomStore.Rooms.FirstOrDefault(r => r.Id == refId) is { } room:
                RoomActivated?.Invoke(room);
                break;
            case "file":
                FileActivated?.Invoke(refId);
                break;
        }
    }

    // ---- header ----

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _project.Name = NameBox.Text.Trim();
        Save();
    }

    private void Description_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _project.Description = DescriptionBox.Text.Trim();
        Save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenInExplorer(_project.Folder);

    private void Graph_Click(object sender, RoutedEventArgs e) => GraphRequested?.Invoke(_project);

    private void InfoExpand_Click(object sender, RoutedEventArgs e)
    {
        EnsureInfoFile();
        FileActivated?.Invoke(InfoPath);
    }

    private void Save()
    {
        _project.Touch();
        ProjectStore.Save(_project);
    }

    // ---- info file ----

    private string InfoPath => Path.Combine(
        string.IsNullOrWhiteSpace(_project.Folder) ? ProjectStore.ProjectsRoot : _project.Folder, "PROJECT.md");

    /// <summary>Makes an empty PROJECT.md if there is none, so the editor has a file to open.</summary>
    private void EnsureInfoFile()
    {
        try
        {
            if (!File.Exists(InfoPath))
            {
                var dir = Path.GetDirectoryName(InfoPath);
                if (dir is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(InfoPath, $"# {_project.Name}\n\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not create project info file: {ex.Message}");
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not open {path}: {ex.Message}");
        }
    }
}
