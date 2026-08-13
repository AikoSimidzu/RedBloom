using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Controls;

/// <summary>
/// Views and edits one file: code with live syntax highlighting, Markdown as a rendered document
/// with an edit mode, and images in a viewer.
/// </summary>
/// <remarks>
/// The surface is a single WebView2 hosting <c>fileview.html</c>, which carries CodeMirror for
/// editing and reuses the chat's <see cref="Markdown"/> renderer for the Markdown preview — so the
/// file reads the same here as an agent's reply does. Which of the three faces it shows is decided
/// from the extension. A file too large or plainly binary is opened read-only rather than mangled.
/// </remarks>
public sealed class FileEditorView : UserControl, IDisposable
{
    private const string VirtualHost = "redbloom.assets";
    private const string PageUrl = $"https://{VirtualHost}/fileview.html";
    private const long MaxTextBytes = 8 * 1024 * 1024;
    private const long MaxImageBytes = 25 * 1024 * 1024;

    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico"];

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedBloom", "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private readonly string _path;
    private readonly WebView2 _webView = new();
    private bool _pageReady;
    private bool _disposed;

    public FileEditorView(string path)
    {
        _path = path;

        var s = ThemeService.Settings;
        var background = ThemeService.ParseColor(s.TerminalBackground, System.Windows.Media.Colors.Black);
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, background.R, background.G, background.B);

        Content = _webView;
        Loaded += OnLoaded;
    }

    private bool IsImage => ImageExts.Contains(Path.GetExtension(_path).ToLowerInvariant());

    private bool IsMarkdown => Path.GetExtension(_path).ToLowerInvariant() is ".md" or ".markdown" or ".mdx";

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
            Content = new TextBlock { Text = $"WebView2 failed to initialize: {ex.Message}", Margin = new Thickness(16) };
            return;
        }

        if (_disposed)
        {
            return;
        }

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, args) => { args.Handled = true; OpenExternally(args.Uri); };
        core.Navigate(PageUrl);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }

        JsonElement m;
        try { m = JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return; }

        switch (m.TryGetProperty("t", out var t) ? t.GetString() : null)
        {
            case "ready":
                _pageReady = true;
                PushTheme();
                PushLabels();
                LoadFile();
                break;

            case "save" when m.TryGetProperty("text", out var text):
                Save(text.GetString() ?? string.Empty);
                break;

            case "renderMarkdown" when m.TryGetProperty("text", out var md):
                Post(new { t = "renderedMarkdown", html = Markdown.ToHtml(md.GetString() ?? string.Empty) });
                break;

            case "pickImage":
                PickImage();
                break;

            case "download" when m.TryGetProperty("path", out var dl):
                Download(dl.GetString() ?? string.Empty);
                break;

            case "copyImage" when m.TryGetProperty("path", out var cp):
                CopyImage(cp.GetString() ?? string.Empty);
                break;
        }
    }

    private void LoadFile()
    {
        if (IsImage)
        {
            LoadImage();
            return;
        }

        try
        {
            var info = new FileInfo(_path);

            if (!info.Exists)
            {
                Post(new { t = "code", name = Path.GetFileName(_path), text = LocalizationService.T("L_FilesGone"), readOnly = true });
                return;
            }

            if (info.Length > MaxTextBytes)
            {
                Post(new { t = "code", name = Path.GetFileName(_path), text = LocalizationService.T("L_FilesTooBig"), readOnly = true });
                return;
            }

            var bytes = File.ReadAllBytes(_path);
            var binary = Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0;
            var content = Encoding.UTF8.GetString(bytes);

            if (IsMarkdown && !binary)
            {
                Post(new { t = "markdown", text = content, html = Markdown.ToHtml(content), readOnly = false });
            }
            else
            {
                Post(new { t = "code", name = Path.GetFileName(_path), text = content, readOnly = binary });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(new { t = "code", name = Path.GetFileName(_path), text = ex.Message, readOnly = true });
        }
    }

    /// <summary>Shows the image with its folder's other images as prev/next, like the chat lightbox.</summary>
    private void LoadImage()
    {
        var siblings = new List<string>();

        try
        {
            var dir = Path.GetDirectoryName(_path);

            if (dir is not null)
            {
                siblings.AddRange(Directory.EnumerateFiles(dir)
                    .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed still shows the one file that was asked for.
        }

        if (siblings.Count == 0)
        {
            siblings.Add(_path);
        }

        var index = Math.Max(0, siblings.FindIndex(f => string.Equals(f, _path, StringComparison.OrdinalIgnoreCase)));

        var list = siblings.Select(f => new { src = ImageDataUri(f), path = f, name = Path.GetFileName(f) });
        Post(new { t = "image", list, index });
    }

    private void Save(string text)
    {
        try
        {
            File.WriteAllText(_path, text);
            AgentFiles.Touched(_path);
            Post(new { t = "saved" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, LocalizationService.T("L_FilesSave"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            // A path relative to the markdown file reads better in it and survives the folder moving.
            var rel = Relative(dialog.FileName);
            Post(new { t = "insertImage", path = rel });
        }
    }

    private string Relative(string target)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            return dir is null ? target : Path.GetRelativePath(dir, target).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return target;
        }
    }

    private void Download(string source)
    {
        if (!File.Exists(source))
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(source) };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                File.Copy(source, dialog.FileName, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, LocalizationService.T("L_FilesSave"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void CopyImage(string source)
    {
        try
        {
            if (File.Exists(source))
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(source, UriKind.Absolute);
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.EndInit();
                Clipboard.SetImage(image);
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // Clipboard can be held by another app; nothing to do but leave it.
        }
    }

    private static string ImageDataUri(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxImageBytes)
            {
                return string.Empty;
            }

            var media = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                _ => "image/png",
            };

            return $"data:{media};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void PushTheme()
    {
        var s = ThemeService.Settings;

        static string C(string v, System.Windows.Media.Color f)
        {
            var c = ThemeService.ParseColor(v, f);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        Post(new
        {
            t = "theme",
            vars = new Dictionary<string, string>
            {
                ["surface"] = C(s.TerminalBackground, System.Windows.Media.Colors.Black),
                ["raised"] = C(s.SurfaceRaised, System.Windows.Media.Colors.Black),
                ["chrome"] = C(s.Chrome, System.Windows.Media.Colors.Black),
                ["divider"] = C(s.Divider, System.Windows.Media.Colors.Gray),
                ["text"] = C(s.TerminalForeground, System.Windows.Media.Colors.White),
                ["muted"] = C(s.TextMuted, System.Windows.Media.Colors.Gray),
                ["faint"] = C(s.TextFaint, System.Windows.Media.Colors.Gray),
                ["accent"] = C(s.Accent, System.Windows.Media.Colors.Red),
                ["accent-dim"] = C(s.AccentDim, System.Windows.Media.Colors.DarkRed),
                ["ui-font"] = s.UiFontFamily,
                ["code-font"] = s.TerminalFontFamily,
                ["code-size"] = $"{s.TerminalFontSize:0.#}px",
            },
        });
    }

    private void PushLabels() => Post(new
    {
        t = "labels",
        labels = new
        {
            preview = LocalizationService.T("L_FilesPreview"),
            edit = LocalizationService.T("L_FilesEdit"),
            bold = LocalizationService.T("L_FilesBold"),
            italic = LocalizationService.T("L_FilesItalic"),
            strike = LocalizationService.T("L_FilesStrike"),
            heading = LocalizationService.T("L_FilesHeading"),
            quote = LocalizationService.T("L_FilesQuote"),
            list = LocalizationService.T("L_FilesList"),
            code = LocalizationService.T("L_FilesInlineCode"),
            codeblock = LocalizationService.T("L_FilesCodeBlock"),
            link = LocalizationService.T("L_FilesLink"),
            image = LocalizationService.T("L_FilesImage"),
        },
    });

    private void Post(object message)
    {
        if (_pageReady && !_disposed)
        {
            _webView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(message));
        }
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser to hand it to; the link simply does not open.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webView.Dispose();
    }
}
