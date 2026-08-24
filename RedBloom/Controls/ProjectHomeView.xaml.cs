using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Controls;

/// <summary>
/// A project's home, drawn as one scrolling page in a WebView2: its name and description, the
/// activity monitoring, the linked sources, the interactive relationship tree and the Markdown info,
/// all in one surface. One WebView2 fixed under the tab strip scrolls its own content, so there is
/// no airspace overlap — unlike several native panels each fighting the scroll.
/// </summary>
public partial class ProjectHomeView : UserControl, IDisposable
{
    private const string VirtualHost = "redbloom.assets";
    private const string PageUrl = $"https://{VirtualHost}/project.html";

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedBloom", "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Project _project;
    private readonly WebView2 _webView = new();
    private bool _ready;
    private bool _disposed;

    public ProjectHomeView(Project project)
    {
        _project = project;
        InitializeComponent();
        Host.Children.Add(_webView);
        Loaded += OnLoaded;
    }

    public event Action<ChatSession>? ChatActivated;
    public event Action<ChatRoom>? RoomActivated;
    public event Action<string>? FileActivated;
    public event Action<Project>? GraphRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var environment = await SharedEnvironment.Value.ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebView2RuntimeNotFoundException)
        {
            Host.Children.Clear();
            Host.Children.Add(new TextBlock { Text = $"WebView2 failed to initialize: {ex.Message}", Margin = new(16) });
            return;
        }

        if (_disposed)
        {
            return;
        }

        // Transparent, so the window's wallpaper shows through the page's empty areas; the graph and
        // info panels paint their own solid background over it.
        _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        ThemeService.Applied += PushTheme;
        IsVisibleChanged += OnVisibleChanged;
        Unloaded += (_, _) =>
        {
            ThemeService.Applied -= PushTheme;
            IsVisibleChanged -= OnVisibleChanged;
        };

        core.Navigate(PageUrl);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _ready)
        {
            PushGraph();
            PushStats();
            PushSources();
            PushGitBadges();
            PushInfo();
        }
    }

    /// <summary>Re-sends the tree, so the inline graph catches up after edits in the expanded window.</summary>
    private void PushGraph() => Post(new { t = "graph", graph = _project.Graph, palette = Palette(), folderTree = ProjectPalette.FolderTree(_project) });

    /// <summary>Re-renders the info preview, so it catches up after edits in the expanded editor.</summary>
    private void PushInfo() => Post(new { t = "infoHtml", html = Markdown.ToHtml(LoadMarkdown()) });

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(e.TryGetWebMessageAsString()).RootElement;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return;
        }

        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        var value = root.TryGetProperty("value", out var vv) ? vv.GetString() ?? string.Empty : string.Empty;

        switch (t)
        {
            case "ready":
                _ready = true;
                PushTheme();
                PushInit();
                PushStats();
                PushGitBadges();
                SetupWatchers();
                break;

            case "name":
                _project.Name = value.Trim();
                Save();
                break;

            case "desc":
                _project.Description = value.Trim();
                Save();
                break;

            case "openFolder":
                Open(_project.Folder);
                break;

            case "saveGraph" when root.TryGetProperty("graph", out var graph):
                SaveGraph(graph);
                break;

            case "openNode" when root.TryGetProperty("kind", out var k) && root.TryGetProperty("refId", out var r):
                OpenNode(k.GetString() ?? string.Empty, r.GetString() ?? string.Empty);
                break;

            case "expandGraph":
                GraphRequested?.Invoke(_project);
                break;

            case "addSource":
                ShowAddSourceMenu();
                break;

            case "openSource" when root.TryGetProperty("id", out var oid):
                OpenSource(oid.GetString() ?? string.Empty);
                break;

            case "openInVs" when root.TryGetProperty("id", out var vid):
                OpenInVs(vid.GetString() ?? string.Empty);
                break;

            case "cloneSource" when root.TryGetProperty("id", out var cid):
                CloneSource(cid.GetString() ?? string.Empty);
                break;

            case "publish":
                PublishProject();
                break;

            case "removeSource" when root.TryGetProperty("id", out var rid):
                var id = rid.GetString() ?? string.Empty;
                _project.Sources.RemoveAll(s => s.Id == id);
                Save();
                PushSources();
                SetupWatchers();
                break;

            case "expandInfo":
                EnsureInfoFile();
                FileActivated?.Invoke(InfoPath);
                break;

            case "exportImage" when root.TryGetProperty("data", out var img):
                SaveGraphImage(img.GetString() ?? string.Empty);
                break;
        }
    }

    // ---- pushes ----

    private void PushInit()
    {
        Post(new
        {
            t = "init",
            project = new { name = _project.Name, description = _project.Description, folder = _project.Folder, published = _project.PublishedRepo.Length > 0 },
            graph = _project.Graph,
            palette = Palette(),
            folderTree = ProjectPalette.FolderTree(_project),
            sources = SourceDtos(),
            infoHtml = Markdown.ToHtml(LoadMarkdown()),
            strings = Strings(),
        });
    }

    private async void PushStats()
    {
        var stats = await ProjectStats.ComputeAsync(_project).ConfigureAwait(true);
        Post(new
        {
            t = "stats",
            stats = new
            {
                chats = stats.Chats,
                chatMessages = stats.ChatMessages,
                rooms = stats.Rooms,
                roomMessages = stats.RoomMessages,
                files = stats.Files,
                size = HumanSize(stats.Bytes),
                loc = stats.Loc,
                languages = stats.Languages.Select(l => new { name = l.Name, lines = l.Lines }).ToList(),
                complexity = stats.Complexity,
                complexityLevel = stats.ComplexityLevel,
                nodes = stats.GraphNodes,
                edges = stats.GraphEdges,
                isolated = stats.Isolated,
                topNode = stats.TopNode,
                last = stats.LastActivity is { } w ? Relative(w) : "—",
                git = stats.Git is { } g ? new { branch = g.Branch, changes = g.Changes, commit = g.LastCommit } : null,
            },
        });
    }

    private void PushSources() => Post(new { t = "sources", sources = SourceDtos() });

    private async void PushGitBadges()
    {
        foreach (var source in _project.Sources.Where(s => s.Path.Length > 0).ToList())
        {
            var git = await ProjectStats.GitAsync(source.Path).ConfigureAwait(true);
            if (git is { } g)
            {
                var text = g.Changes == 0 ? g.Branch : $"{g.Branch} · {string.Format(LocalizationService.T("L_StatChanges"), g.Changes)}";
                Post(new { t = "gitBadge", id = source.Id, text });
            }
        }
    }

    private object SourceDtos() => _project.Sources
        .Select(s => new { id = s.Id, kind = s.Kind.ToString(), name = s.Name, path = s.Path, repo = s.Repo, url = s.Url })
        .ToList();

    private object Palette() =>
        ProjectPalette.Build(_project).Select(i => new { kind = i.Kind, refId = i.RefId, label = i.Label }).ToList();

    private void PushTheme()
    {
        var s = ThemeService.Settings;
        Post(new
        {
            t = "theme",
            vars = new Dictionary<string, string>
            {
                ["surface"] = s.TerminalBackground,
                ["surface2"] = s.TerminalBackground,
                ["raised"] = s.SurfaceRaised,
                ["chrome"] = s.Chrome,
                ["divider"] = s.Divider,
                ["text"] = s.TerminalForeground,
                ["muted"] = s.TextMuted,
                ["faint"] = s.TextFaint,
                ["accent"] = s.Accent,
                ["accent-dim"] = s.AccentDim,
                ["ui-font"] = s.UiFontFamily,
                ["code-font"] = s.TerminalFontFamily,
            },
        });
    }

    private static object Strings() => new
    {
        openFolder = LocalizationService.T("L_ProjectOpenFolder"),
        secSources = LocalizationService.T("L_SourcesTitle"),
        srcAdd = LocalizationService.T("L_SourcesAdd"),
        srcNone = LocalizationService.T("L_SourcesNone"),
        srcOpen = LocalizationService.T("L_SourceOpen"),
        srcRemove = LocalizationService.T("L_SourceRemove"),
        srcOpenVs = LocalizationService.T("L_SourceOpenVs"),
        srcClone = LocalizationService.T("L_SourceClone"),
        publish = LocalizationService.T("L_Publish"),
        update = LocalizationService.T("L_PublishUpdate"),
        statLoc = LocalizationService.T("L_StatLoc"),
        secGraph = LocalizationService.T("L_ProjectGraph"),
        expand = LocalizationService.T("L_ProjectExpand"),
        fit = LocalizationService.T("L_GraphFit"),
        exportImage = LocalizationService.T("L_GraphExport"),
        ghint = LocalizationService.T("L_GraphHint"),
        secInfo = LocalizationService.T("L_ProjectInfo"),
        infoEmpty = LocalizationService.T("L_ProjectInfoEmpty"),
        statChats = LocalizationService.T("L_StatChats"),
        statRooms = LocalizationService.T("L_StatRooms"),
        statFiles = LocalizationService.T("L_StatFiles"),
        statClean = LocalizationService.T("L_StatClean"),
        statChanges = LocalizationService.T("L_StatChanges"),
        statLast = LocalizationService.T("L_StatLastActivity"),
        statComplexity = LocalizationService.T("L_StatComplexity"),
        statConnections = LocalizationService.T("L_StatConnections"),
        statNodes = LocalizationService.T("L_StatNodes"),
        statIsolated = LocalizationService.T("L_StatIsolated"),
        statTop = LocalizationService.T("L_StatTopNode"),
        cx = new
        {
            low = LocalizationService.T("L_CxLow"),
            medium = LocalizationService.T("L_CxMedium"),
            high = LocalizationService.T("L_CxHigh"),
            veryhigh = LocalizationService.T("L_CxVeryHigh"),
        },
        node = LocalizationService.T("L_GraphNode"),
        note = LocalizationService.T("L_GraphNote"),
        label = LocalizationService.T("L_GraphLabel"),
        desc = LocalizationService.T("L_GraphDesc"),
        color = LocalizationService.T("L_GraphColor"),
        customColor = LocalizationService.T("L_GraphCustomColor"),
        removeColor = LocalizationService.T("L_GraphRemoveColor"),
        conn = LocalizationService.T("L_GraphConnection"),
        width = LocalizationService.T("L_GraphWidth"),
        dashed = LocalizationService.T("L_GraphDashed"),
        arrow = LocalizationService.T("L_GraphArrow"),
        open = LocalizationService.T("L_GraphOpen"),
        del = LocalizationService.T("L_Delete"),
        kinds = new
        {
            note = LocalizationService.T("L_GraphKindNote"),
            chat = LocalizationService.T("L_GraphKindChat"),
            room = LocalizationService.T("L_GraphKindRoom"),
            file = LocalizationService.T("L_GraphKindFile"),
            source = LocalizationService.T("L_GraphKindSource"),
            folder = LocalizationService.T("L_GraphKindFolder"),
            milestone = LocalizationService.T("L_GraphKindMilestone"),
        },
    };

    // ---- actions ----

    private void Save()
    {
        _project.Touch();
        ProjectStore.Save(_project);
    }

    private void SaveGraph(JsonElement graph)
    {
        try
        {
            if (graph.Deserialize<ProjectGraph>(JsonOptions) is { } parsed)
            {
                _project.Graph = parsed;
                Save();
            }
        }
        catch (JsonException)
        {
            // A malformed payload is dropped rather than overwriting the saved graph.
        }
    }

    private void OpenNode(string kind, string refId)
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
            case "source":
                OpenSource(refId);
                break;
            case "folder":
                Open(refId);
                break;
        }
    }

    private string InfoPath => Path.Combine(
        string.IsNullOrWhiteSpace(_project.Folder) ? ProjectStore.ProjectsRoot : _project.Folder, "PROJECT.md");

    private string LoadMarkdown()
    {
        try
        {
            return File.Exists(InfoPath) ? File.ReadAllText(InfoPath) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Makes an empty PROJECT.md if there is none, so the expanded editor has a file to open.</summary>
    private void EnsureInfoFile()
    {
        try
        {
            if (File.Exists(InfoPath))
            {
                return;
            }

            var dir = Path.GetDirectoryName(InfoPath);
            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(InfoPath, $"# {_project.Name}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not create project info file: {ex.Message}");
        }
    }

    // ---- sources ----

    private void ShowAddSourceMenu()
    {
        var menu = new ContextMenu();

        void Add(string key, Action run)
        {
            var item = new MenuItem { Header = LocalizationService.T(key) };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        Add("L_SourceLocal", AddLocal);
        Add("L_SourceVs", AddVs);
        Add("L_SourceGitHub", AddGitHub);

        menu.PlacementTarget = _webView;
        menu.IsOpen = true;
    }

    private void AddLocal()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = LocalizationService.T("L_SourceLocal") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AddSource(new ProjectSource
            {
                Kind = SourceKind.Local,
                Name = Path.GetFileName(dialog.FolderName.TrimEnd('\\', '/')),
                Path = dialog.FolderName,
            });
        }
    }

    private async void AddVs()
    {
        var solutions = await VisualStudioSources.DiscoverAsync().ConfigureAwait(true);
        if (Views.VsSolutionsDialog.Pick(Window.GetWindow(this), solutions) is { } chosen)
        {
            AddSource(new ProjectSource { Kind = SourceKind.VisualStudio, Name = chosen.Name, Path = chosen.Folder });
        }
    }

    private void AddGitHub()
    {
        if (Views.GitHubDialog.Pick(Window.GetWindow(this)) is { } repo)
        {
            AddSource(new ProjectSource { Kind = SourceKind.GitHub, Name = repo.Name, Repo = repo.FullName, Url = repo.Url });
        }
    }

    private void AddSource(ProjectSource source)
    {
        _project.Sources.Add(source);
        Save();
        PushSources();
        PushGitBadges();
        SetupWatchers();
    }

    private void OpenSource(string id)
    {
        if (_project.Sources.FirstOrDefault(s => s.Id == id) is not { } source)
        {
            return;
        }

        Open(source.Path.Length > 0 ? source.Path : source.Url);
    }

    /// <summary>Opens a source's solution in Visual Studio, or its folder when no solution is found.</summary>
    private void OpenInVs(string id)
    {
        if (_project.Sources.FirstOrDefault(s => s.Id == id) is not { Path.Length: > 0 } source)
        {
            return;
        }

        try
        {
            var sln = Directory.Exists(source.Path)
                ? Directory.EnumerateFiles(source.Path, "*.sln").FirstOrDefault()
                : File.Exists(source.Path) ? source.Path : null;

            // The .sln opens in Visual Studio through the shell association; a folder just opens.
            Process.Start(new ProcessStartInfo(sln ?? source.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"Could not open in VS: {ex.Message}");
        }
    }

    /// <summary>Clones a linked GitHub repository into the project folder and tracks it locally.</summary>
    private async void CloneSource(string id)
    {
        if (_project.Sources.FirstOrDefault(s => s.Id == id) is not { Kind: SourceKind.GitHub } source
            || string.IsNullOrWhiteSpace(_project.Folder))
        {
            return;
        }

        var wasConnected = GitHubClient.IsConnected;
        if (!wasConnected || !await GitHubClient.EnsureValidAsync())
        {
            MessageBox.Show(Window.GetWindow(this),
                LocalizationService.T(wasConnected ? "L_GhReauth" : "L_GhNotConnected"),
                "GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var token = GitHubClient.CurrentToken();
        var cloneUrl = source.Repo.Length > 0 ? $"https://github.com/{source.Repo}.git" : source.Url;
        var target = Path.Combine(_project.Folder, SafeName(source.Name));

        var (path, error) = await GitOps.CloneAsync(cloneUrl, token, target).ConfigureAwait(true);

        if (path is null)
        {
            MessageBox.Show(Window.GetWindow(this), error ?? "Clone failed.", "GitHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        source.Path = path;
        Save();
        PushSources();
        PushGitBadges();
        SetupWatchers();
    }

    /// <summary>
    /// Publishes the project to a new private GitHub repository the first time, and on later calls
    /// commits and pushes the changes to that same repository.
    /// </summary>
    private async void PublishProject()
    {
        var wasConnected = GitHubClient.IsConnected;
        if (!wasConnected || !await GitHubClient.EnsureValidAsync())
        {
            MessageBox.Show(Window.GetWindow(this),
                LocalizationService.T(wasConnected ? "L_GhReauth" : "L_GhNotConnected"),
                "GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_project.Folder) || !Directory.Exists(_project.Folder))
        {
            return;
        }

        if (_project.PublishedRepo.Length > 0)
        {
            await UpdateRepo();
        }
        else
        {
            await PublishNew();
        }
    }

    private async Task PublishNew()
    {
        var changes = await GitOps.ChangesAsync(_project.Folder).ConfigureAwait(true);
        if (Views.PublishDialog.Show(Window.GetWindow(this), isUpdate: false, SafeName(_project.Name), string.Empty, changes) is not { } choice)
        {
            return;
        }

        ExportMetadata();
        WriteGitIgnore(choice.ImportAll);

        var repo = await GitHubClient.CreateRepoAsync(SafeName(choice.Name), choice.Private).ConfigureAwait(true);
        if (repo is not { } r)
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationService.T("L_PublishRepoFailed"), "GitHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var error = await GitOps.PublishAsync(_project.Folder, r.CloneUrl, GitHubClient.CurrentToken(), choice.Message).ConfigureAwait(true);
        if (error is not null)
        {
            MessageBox.Show(Window.GetWindow(this), error, LocalizationService.T("L_PublishTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _project.PublishedRepo = r.FullName;
        _project.Sources.Add(new ProjectSource { Kind = SourceKind.GitHub, Name = r.FullName, Repo = r.FullName, Url = r.HtmlUrl, Path = _project.Folder });
        Save();
        PushSources();
        PushGitBadges();
        Post(new { t = "published" });

        MessageBox.Show(Window.GetWindow(this), string.Format(LocalizationService.T("L_PublishDone"), r.FullName),
            LocalizationService.T("L_PublishTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task UpdateRepo()
    {
        var changes = await GitOps.ChangesAsync(_project.Folder).ConfigureAwait(true);
        if (Views.PublishDialog.Show(Window.GetWindow(this), isUpdate: true, string.Empty, _project.PublishedRepo, changes) is not { } choice)
        {
            return;
        }

        ExportMetadata();
        WriteGitIgnore(choice.ImportAll);

        var cloneUrl = $"https://github.com/{_project.PublishedRepo}.git";
        var error = await GitOps.PublishAsync(_project.Folder, cloneUrl, GitHubClient.CurrentToken(), choice.Message).ConfigureAwait(true);

        MessageBox.Show(Window.GetWindow(this),
            error ?? string.Format(LocalizationService.T("L_UpdateDone"), _project.PublishedRepo),
            LocalizationService.T("L_PublishTitle"), MessageBoxButton.OK, error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);

        PushGitBadges();
    }

    /// <summary>
    /// Writes the .gitignore that decides what the publish carries. With <paramref name="importAll"/>
    /// everything ships except build output (bin/obj/.vs), the way Visual Studio publishes. Otherwise
    /// only the project's own data goes out — its connections and description (in .redbloom), the
    /// PROJECT.md notes, and the chats and rooms — and all source code is left behind.
    /// </summary>
    private void WriteGitIgnore(bool importAll)
    {
        try
        {
            var path = Path.Combine(_project.Folder, ".gitignore");
            var lines = importAll
                ? new List<string>
                {
                    "# RedBloom publish: everything except build output",
                    "bin/", "obj/", ".vs/", "*.user",
                }
                : new List<string>
                {
                    "# RedBloom publish: project data only (connections, notes, chats, rooms)",
                    "/*",
                    "!/.gitignore",
                    "!/.redbloom/",
                    "!/PROJECT.md",
                    "!/README.md",
                    "!/.chats/",
                    "!/.rooms/",
                };

            File.WriteAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not write .gitignore: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the project's connections and description into the folder as <c>.redbloom/project.json</c>,
    /// so a publish carries them as real files — they otherwise live only in RedBloom's own store.
    /// </summary>
    private void ExportMetadata()
    {
        try
        {
            var dir = Path.Combine(_project.Folder, ".redbloom");
            Directory.CreateDirectory(dir);

            var data = new
            {
                name = _project.Name,
                description = _project.Description,
                updatedAt = DateTime.Now,
                graph = _project.Graph,
                sources = _project.Sources.Select(s => new { kind = s.Kind.ToString(), name = s.Name, repo = s.Repo, url = s.Url }).ToList(),
            };

            File.WriteAllText(
                Path.Combine(dir, "project.json"),
                JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not export project metadata: {ex.Message}");
        }
    }

    /// <summary>Saves a data-URL PNG from the graph to a file the user picks, then opens it.</summary>
    private void SaveGraphImage(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.T("L_GraphExport"),
            Filter = "PNG image (*.png)|*.png",
            FileName = $"{SafeName(_project.Name)}-tree.png",
            DefaultExt = ".png",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, LocalizationService.T("L_GraphExport"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string SafeName(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var safe = new string(chars).Trim('-');
        return safe.Length > 0 ? safe : "project";
    }

    // ---- live file watching ----

    private readonly List<FileSystemWatcher> _watchers = [];
    private System.Windows.Threading.DispatcherTimer? _watchTimer;

    private void SetupWatchers()
    {
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();

        _watchTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _watchTimer.Tick -= OnWatchTick;
        _watchTimer.Tick += OnWatchTick;

        var dirs = new List<string> { _project.Folder };
        dirs.AddRange(_project.Sources.Select(s => s.Path).Where(p => p.Length > 0));

        foreach (var dir in dirs.Where(d => d.Length > 0 && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                watcher.Changed += OnFsChanged;
                watcher.Created += OnFsChanged;
                watcher.Deleted += OnFsChanged;
                watcher.Renamed += OnFsChanged;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception)
            {
                // A folder we cannot watch is simply not watched.
            }
        }
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(() => { _watchTimer?.Stop(); _watchTimer?.Start(); });

    private void OnWatchTick(object? sender, EventArgs e)
    {
        _watchTimer?.Stop();
        if (_ready && !_disposed)
        {
            PushStats();
            PushGitBadges();
        }
    }

    // ---- helpers ----

    private static void Open(string target)
    {
        try
        {
            if (target.Length > 0 && (Directory.Exists(target) || target.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not open {target}: {ex.Message}");
        }
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

    private void Post(object message)
    {
        if (!_ready || _disposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message, JsonOptions));
    }

    public void Dispose()
    {
        _disposed = true;
        _watchTimer?.Stop();
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
        _webView.Dispose();
    }
}
