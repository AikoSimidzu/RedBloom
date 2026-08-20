using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Services;

namespace RedBloom.Controls;

/// <summary>
/// Hosts one extension's HTML page in a WebView2 and gives it a small, declared bridge to the host:
/// running the programs the manifest lists (streamed, cancellable) and reading and writing files in
/// the extension's own data folder. Everything is confined — a page cannot run a program it did not
/// declare, and cannot touch a file outside its data folder.
/// </summary>
public partial class ExtensionView : UserControl, IDisposable
{
    // Shared host serves the bundled vendor libraries (CodeMirror &c.); the ext host serves the
    // extension's own files. The page loads its files relatively and vendor libs by absolute URL.
    private const string AssetsHost = "redbloom.assets";
    private const string ExtHost = "redbloom.ext";

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedBloom", "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ExtensionStore.Extension _ext;
    private readonly WebView2 _webView = new();
    private readonly Dictionary<string, Process> _running = new();
    private bool _ready;
    private bool _disposed;

    public ExtensionView(ExtensionStore.Extension extension)
    {
        _ext = extension;
        InitializeComponent();
        Host.Children.Add(_webView);
        Loaded += OnLoaded;
    }

    /// <summary>The hosted extension's id, so the window can avoid opening a second tab for it.</summary>
    public string ExtensionId => _ext.Id;

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

        try
        {
            Directory.CreateDirectory(_ext.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not create extension data folder: {ex.Message}");
        }

        _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            AssetsHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(
            ExtHost, _ext.Root, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        ThemeService.Applied += PushTheme;
        Unloaded += (_, _) => ThemeService.Applied -= PushTheme;

        core.Navigate($"https://{ExtHost}/{_ext.Manifest.Entry.Replace('\\', '/')}");
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

        var t = root.TryGetProperty("t", out var tv) ? tv.GetString() : null;
        var id = root.TryGetProperty("id", out var iv) ? iv.GetString() ?? string.Empty : string.Empty;

        switch (t)
        {
            case "ready":
                _ready = true;
                PushTheme();
                PushInit();
                break;

            case "exec":
                Exec(id,
                    root.TryGetProperty("program", out var pg) ? pg.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("args", out var ar) && ar.ValueKind == JsonValueKind.Array
                        ? ar.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToArray()
                        : [],
                    root.TryGetProperty("cwd", out var cw) ? cw.GetString() : null);
                break;

            case "exec.cancel":
                Cancel(id);
                break;

            case "fs.list":
                FsList(id, Rel(root));
                break;

            case "fs.read":
                FsRead(id, Rel(root));
                break;

            case "fs.write":
                FsWrite(id, Rel(root), root.TryGetProperty("text", out var wt) ? wt.GetString() ?? string.Empty : string.Empty);
                break;

            case "fs.mkdir":
                FsMkdir(id, Rel(root));
                break;

            case "openFolder":
                OpenFolder(Rel(root));
                break;
        }
    }

    private static string Rel(JsonElement root) =>
        root.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;

    // ---- init / theme ----

    private void PushInit() => Post(new
    {
        t = "init",
        lang = LocalizationService.IsRussian ? "ru" : "en",
        dataDir = _ext.DataDir,
        extension = new { id = _ext.Id, name = _ext.Manifest.Name, version = _ext.Manifest.Version },
    });

    private void PushTheme()
    {
        var s = ThemeService.Settings;
        Post(new
        {
            t = "theme",
            vars = new Dictionary<string, string>
            {
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
                ["code-font"] = s.TerminalFontFamily,
            },
        });
    }

    // ---- exec ----

    private async void Exec(string id, string program, string[] args, string? cwd)
    {
        if (id.Length == 0)
        {
            return;
        }

        if (!_ext.Manifest.Programs.Contains(program, StringComparer.OrdinalIgnoreCase))
        {
            Post(new { t = "exec.done", id, code = -1, error = $"This extension is not allowed to run '{program}'." });
            return;
        }

        if (ExtensionStore.ResolveProgram(program) is not { } exe)
        {
            Post(new { t = "exec.done", id, code = -1, error = $"'{program}' is not installed or not on PATH." });
            return;
        }

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ConfineDir(cwd),
        };

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("process did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Post(new { t = "exec.done", id, code = -1, error = ex.Message });
            return;
        }

        _running[id] = process;

        // Both pipes are drained at once — a chatty stderr must not be able to wedge the child.
        var outTask = PumpAsync(process.StandardOutput, id, "stdout");
        var errTask = PumpAsync(process.StandardError, id, "stderr");
        await Task.WhenAll(outTask, errTask).ConfigureAwait(true);
        await process.WaitForExitAsync().ConfigureAwait(true);

        var code = process.ExitCode;
        _running.Remove(id);
        process.Dispose();

        Post(new { t = "exec.done", id, code });
    }

    private async Task PumpAsync(StreamReader reader, string id, string stream)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(true) is { } line)
        {
            if (_disposed)
            {
                return;
            }

            Post(new { t = "exec.out", id, stream, text = line });
        }
    }

    private void Cancel(string id)
    {
        if (_running.TryGetValue(id, out var process))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }

    // ---- files (confined to the extension's data folder) ----

    /// <summary>Resolves a relative path against the data folder, refusing anything that escapes it.</summary>
    private string? Confine(string relative)
    {
        try
        {
            var root = Path.GetFullPath(_ext.DataDir);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full == root || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private string ConfineDir(string? relative) =>
        Confine(relative ?? string.Empty) is { } dir && Directory.Exists(dir) ? dir : _ext.DataDir;

    private void FsList(string id, string relative)
    {
        if (Confine(relative) is not { } dir || !Directory.Exists(dir))
        {
            Post(new { t = "fs.list.result", id, entries = Array.Empty<object>() });
            return;
        }

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(dir)
                .Select(p => new { name = Path.GetFileName(p), dir = Directory.Exists(p) })
                .OrderByDescending(x => x.dir).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Post(new { t = "fs.list.result", id, entries });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(new { t = "fs.list.result", id, entries = Array.Empty<object>(), error = ex.Message });
        }
    }

    private void FsRead(string id, string relative)
    {
        if (Confine(relative) is not { } file || !File.Exists(file))
        {
            Post(new { t = "fs.read.result", id, text = string.Empty, ok = false });
            return;
        }

        try
        {
            Post(new { t = "fs.read.result", id, text = File.ReadAllText(file), ok = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(new { t = "fs.read.result", id, text = string.Empty, ok = false, error = ex.Message });
        }
    }

    private void FsWrite(string id, string relative, string text)
    {
        if (Confine(relative) is not { } file)
        {
            Post(new { t = "fs.write.result", id, ok = false, error = "Path is outside the extension folder." });
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(file);
            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(file, text);
            Post(new { t = "fs.write.result", id, ok = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(new { t = "fs.write.result", id, ok = false, error = ex.Message });
        }
    }

    private void FsMkdir(string id, string relative)
    {
        if (Confine(relative) is not { } dir)
        {
            Post(new { t = "fs.mkdir.result", id, ok = false });
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            Post(new { t = "fs.mkdir.result", id, ok = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(new { t = "fs.mkdir.result", id, ok = false, error = ex.Message });
        }
    }

    private void OpenFolder(string relative)
    {
        var target = Confine(relative) ?? _ext.DataDir;
        try
        {
            if (!Directory.Exists(target))
            {
                target = _ext.DataDir;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"Could not open folder: {ex.Message}");
        }
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

        foreach (var process in _running.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        _running.Clear();
        _webView.Dispose();
    }
}
