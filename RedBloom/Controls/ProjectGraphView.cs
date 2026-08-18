using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Controls;

/// <summary>
/// A project's relationship tree, drawn on a pan-and-zoom canvas: the chats, rooms, files and notes
/// it gathers, and the connections between them. Hosted in a WebView2 (like the chat) because the
/// editing — dragging cards, drawing links, styling a line — is what a canvas does well.
/// </summary>
public sealed class ProjectGraphView : UserControl, IDisposable
{
    private const string VirtualHost = "redbloom.assets";
    private const string PageUrl = $"https://{VirtualHost}/graph.html";

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedBloom", "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Project _project;
    private readonly WebView2 _webView = new();
    private bool _ready;
    private bool _disposed;

    public ProjectGraphView(Project project)
    {
        _project = project;
        Content = _webView;
        Loaded += OnLoaded;
    }

    /// <summary>Raised when a linked node is opened — a chat, room or file the project holds.</summary>
    public event Action<string, string>? OpenRequested;

    /// <summary>Re-sends the graph and palette, so an embedded copy catches up after edits elsewhere.</summary>
    public void Reload()
    {
        if (_ready)
        {
            PushInit();
        }
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var environment = await SharedEnvironment.Value.ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebView2RuntimeNotFoundException)
        {
            Content = new TextBlock { Text = $"WebView2 failed to initialize: {ex.Message}", Margin = new(16) };
            return;
        }

        if (_disposed)
        {
            return;
        }

        // A dark default, so the control never flashes white before the page paints its own.
        var bg = ThemeService.ParseColor(ThemeService.Settings.TerminalBackground, System.Windows.Media.Colors.Black);
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.R, bg.G, bg.B);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        ThemeService.Applied += PushTheme;
        Unloaded += (_, _) => ThemeService.Applied -= PushTheme;

        core.Navigate(PageUrl);
    }

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

        switch (root.TryGetProperty("t", out var t) ? t.GetString() : null)
        {
            case "ready":
                _ready = true;
                PushTheme();
                PushInit();
                break;

            case "save" when root.TryGetProperty("graph", out var graph):
                SaveGraph(graph);
                break;

            case "open" when root.TryGetProperty("kind", out var kind) && root.TryGetProperty("refId", out var refId):
                OpenRequested?.Invoke(kind.GetString() ?? string.Empty, refId.GetString() ?? string.Empty);
                break;
        }
    }

    private void SaveGraph(JsonElement graph)
    {
        try
        {
            if (graph.Deserialize<ProjectGraph>(JsonOptions) is { } parsed)
            {
                _project.Graph = parsed;
                _project.Touch();
                ProjectStore.Save(_project);
            }
        }
        catch (JsonException)
        {
            // A malformed payload is dropped rather than overwriting the saved graph with nothing.
        }
    }

    /// <summary>Hands the page the graph and the pieces of the project it can drop onto the canvas.</summary>
    private void PushInit()
    {
        var palette = new List<object>();

        foreach (var chat in ChatStore.Chats.Where(c => c.ProjectId == _project.Id))
        {
            palette.Add(new { kind = "chat", refId = chat.Id, label = chat.Title });
        }

        foreach (var room in RoomStore.Rooms.Where(r => r.ProjectId == _project.Id))
        {
            palette.Add(new { kind = "room", refId = room.Id, label = room.Title });
        }

        foreach (var file in ProjectFiles())
        {
            palette.Add(new { kind = "file", refId = file, label = Path.GetFileName(file) });
        }

        Post(new
        {
            t = "init",
            graph = _project.Graph,
            palette,
            strings = Strings(),
        });
    }

    private IEnumerable<string> ProjectFiles()
    {
        if (string.IsNullOrWhiteSpace(_project.Folder) || !Directory.Exists(_project.Folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(_project.Folder)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static object Strings() => new
    {
        node = LocalizationService.T("L_GraphNode"),
        note = LocalizationService.T("L_GraphNote"),
        label = LocalizationService.T("L_GraphLabel"),
        desc = LocalizationService.T("L_GraphDesc"),
        connection = LocalizationService.T("L_GraphConnection"),
        width = LocalizationService.T("L_GraphWidth"),
        dashed = LocalizationService.T("L_GraphDashed"),
        arrow = LocalizationService.T("L_GraphArrow"),
        color = LocalizationService.T("L_GraphColor"),
        none = LocalizationService.T("L_GraphDefault"),
        open = LocalizationService.T("L_GraphOpen"),
        del = LocalizationService.T("L_Delete"),
        fit = LocalizationService.T("L_GraphFit"),
        edgeHint = LocalizationService.T("L_GraphHint"),
        kinds = new
        {
            note = LocalizationService.T("L_GraphKindNote"),
            chat = LocalizationService.T("L_GraphKindChat"),
            room = LocalizationService.T("L_GraphKindRoom"),
            file = LocalizationService.T("L_GraphKindFile"),
            milestone = LocalizationService.T("L_GraphKindMilestone"),
        },
    };

    private void PushTheme()
    {
        var s = ThemeService.Settings;

        Post(new
        {
            t = "theme",
            vars = new Dictionary<string, string>
            {
                ["page"] = "transparent",
                ["surface"] = s.TerminalBackground,
                ["raised"] = s.SurfaceRaised,
                ["chrome"] = s.Chrome,
                ["divider"] = s.Divider,
                ["text"] = s.TerminalForeground,
                ["muted"] = s.TextMuted,
                ["faint"] = s.TextFaint,
                ["accent"] = s.Accent,
                ["accent-dim"] = s.AccentDim,
                ["ui-font"] = s.UiFontFamily,
            },
        });
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
        _webView.Dispose();
    }
}
