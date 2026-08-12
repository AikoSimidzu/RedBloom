using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Controls;

/// <summary>
/// A plain text editor for one file, with a rendered preview for Markdown and syntax-coloured code.
/// </summary>
/// <remarks>
/// The editor is a text box; the preview is a small WebView2 shown on demand, rendering the file
/// through the same <see cref="Markdown"/> and <see cref="CodeHighlighter"/> the chat uses — a
/// Markdown file as its formatted self, any other file as one highlighted block. It is for reading
/// and touching up what an agent produced, not for standing in for a real editor, so a file too
/// large or plainly binary is shown read-only rather than mangled.
/// </remarks>
public sealed class FileEditorView : UserControl
{
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>Preview is skipped past this, where building and rendering the HTML stops being cheap.</summary>
    private const int MaxPreviewChars = 400_000;

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedBloom", "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private readonly string _path;
    private readonly bool _isMarkdown;
    private readonly Grid _body;
    private readonly TextBox _editor;
    private readonly TextBlock _status;
    private readonly Button _previewButton;

    private WebView2? _preview;
    private bool _showingPreview;
    private bool _readOnly;

    public FileEditorView(string path)
    {
        _path = path;
        _isMarkdown = Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdx";

        var root = new DockPanel { LastChildFill = true, Background = Brush("Surface", Colors.Black) };

        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 8, 10, 8) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);

        _previewButton = new Button
        {
            Content = LocalizationService.T("L_FilesPreview"),
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(0, 0, 6, 0),
        };
        _previewButton.Click += (_, _) => TogglePreview();
        buttons.Children.Add(_previewButton);

        var save = new Button
        {
            Content = LocalizationService.T("L_FilesSave"),
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(12, 5, 12, 5),
        };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);

        bar.Children.Add(buttons);

        _status = new TextBlock
        {
            Foreground = Brush("TextFaint", Colors.Gray),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = path,
        };
        bar.Children.Add(_status);

        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12.5,
            Background = Brush("Surface", Colors.Black),
            Foreground = Brush("TextPrimary", Colors.White),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 10),
        };

        _body = new Grid();
        _body.Children.Add(_editor);
        root.Children.Add(_body);

        Content = root;

        Load();

        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Save();
            }
        };
    }

    private void Load()
    {
        try
        {
            var info = new FileInfo(_path);

            if (!info.Exists)
            {
                Fail(LocalizationService.T("L_FilesGone"));
                return;
            }

            if (info.Length > MaxBytes)
            {
                Fail(LocalizationService.T("L_FilesTooBig"));
                return;
            }

            var bytes = File.ReadAllBytes(_path);
            _editor.Text = Encoding.UTF8.GetString(bytes);

            // A NUL byte in the first stretch is the plain sign of a binary file; editing it as text
            // would corrupt it on save, so it is shown read-only.
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
            {
                Fail(LocalizationService.T("L_FilesBinary"));
                return;
            }

            _status.Text = _path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    private void Fail(string message)
    {
        _readOnly = true;
        _editor.IsReadOnly = true;
        _status.Text = message;
    }

    private void Save()
    {
        if (_readOnly)
        {
            return;
        }

        try
        {
            File.WriteAllText(_path, _editor.Text);
            AgentFiles.Touched(_path);
            _status.Text = LocalizationService.T("L_FilesSaved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    // ---- preview ----

    private async void TogglePreview()
    {
        _showingPreview = !_showingPreview;
        _previewButton.Content = LocalizationService.T(_showingPreview ? "L_FilesEdit" : "L_FilesPreview");

        if (!_showingPreview)
        {
            if (_preview is not null)
            {
                _preview.Visibility = Visibility.Collapsed;
            }

            _editor.Visibility = Visibility.Visible;
            return;
        }

        _editor.Visibility = Visibility.Collapsed;

        if (_editor.Text.Length > MaxPreviewChars)
        {
            // Too large to render at speed; the editor is the better view for it anyway.
            _showingPreview = false;
            _editor.Visibility = Visibility.Visible;
            _previewButton.Content = LocalizationService.T("L_FilesPreview");
            _status.Text = LocalizationService.T("L_FilesTooBig");
            return;
        }

        await EnsurePreviewAsync().ConfigureAwait(true);

        if (_preview is not null)
        {
            _preview.Visibility = Visibility.Visible;
            _preview.NavigateToString(BuildHtml(_editor.Text));
        }
    }

    private async Task EnsurePreviewAsync()
    {
        if (_preview is not null)
        {
            return;
        }

        var surface = ThemeService.ParseColor(ThemeService.Settings.TerminalBackground, Colors.Black);

        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, surface.R, surface.G, surface.B),
        };
        _body.Children.Add(view);

        try
        {
            var environment = await SharedEnvironment.Value.ConfigureAwait(true);
            await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebView2RuntimeNotFoundException)
        {
            _body.Children.Remove(view);
            _status.Text = ex.Message;
            return;
        }

        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        view.CoreWebView2.Settings.IsStatusBarEnabled = false;

        // Links open in the user's browser, never in this pane.
        view.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };

        _preview = view;
    }

    /// <summary>
    /// The file as a self-contained page: a Markdown file rendered, any other file as one
    /// syntax-coloured block. Everything is built through the escaping renderers, so the file's own
    /// content can never become markup on the page.
    /// </summary>
    private string BuildHtml(string text)
    {
        var body = _isMarkdown
            ? Markdown.ToHtml(text)
            : "<pre class=\"code\"><code>" + CodeHighlighter.Highlight(text) + "</code></pre>";

        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" + Css() + "</style></head><body>"
            + body + "</body></html>";
    }

    private static string Css()
    {
        var s = ThemeService.Settings;
        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string C(string value, Color fallback) => Hex(ThemeService.ParseColor(value, fallback));

        var surface = C(s.TerminalBackground, Colors.Black);
        var raised = C(s.SurfaceRaised, Colors.Black);
        var chrome = C(s.Chrome, Colors.Black);
        var divider = C(s.Divider, Colors.Gray);
        var fg = C(s.TerminalForeground, Colors.White);
        var muted = C(s.TextMuted, Colors.Gray);
        var faint = C(s.TextFaint, Colors.Gray);
        var accent = C(s.Accent, Colors.Red);
        var accentDim = C(s.AccentDim, Colors.DarkRed);
        var ui = s.UiFontFamily;
        var mono = s.TerminalFontFamily;

        return $$"""
            :root { color-scheme: dark; --tok-com: {{faint}}; }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 16px 18px; background: {{surface}}; color: {{fg}};
                   font-family: "{{ui}}", "Segoe UI", sans-serif; font-size: 14px; line-height: 1.55; }
            h1,h2,h3,h4,h5 { color: {{fg}}; line-height: 1.25; margin: 1.2em 0 .5em; }
            h1 { font-size: 1.6em; border-bottom: 1px solid {{divider}}; padding-bottom: .3em; }
            h2 { font-size: 1.35em; border-bottom: 1px solid {{divider}}; padding-bottom: .25em; }
            h3 { font-size: 1.15em; }
            a { color: {{accent}}; }
            p, li { margin: .4em 0; }
            ul, ol { padding-left: 1.5em; }
            hr { border: none; border-top: 1px solid {{divider}}; margin: 1.4em 0; }
            code { font-family: "{{mono}}", Consolas, monospace; font-size: .9em;
                   background: {{raised}}; padding: .15em .35em; border-radius: 4px; }
            code.mention { color: {{accent}}; background: none; font-weight: 600; }
            pre.code, figure.code { background: {{raised}}; border: 1px solid {{divider}};
                                    border-radius: 8px; overflow: hidden; margin: 1em 0; }
            pre.code { overflow: auto; padding: 12px 14px; }
            pre.code code, figure.code code { background: none; padding: 0;
                   font-family: "{{mono}}", Consolas, monospace; font-size: 13px; line-height: 1.5; }
            figure.code figcaption { display: flex; justify-content: space-between; align-items: center;
                   padding: 6px 12px; border-bottom: 1px solid {{divider}}; font-size: 11px; color: {{muted}}; }
            figure.code .tools button { display: none; }
            figure.code pre { margin: 0; padding: 12px 14px; overflow: auto; }
            figure.code.collapsed pre { max-height: none; }
            blockquote { border-left: 3px solid {{accentDim}}; margin: 1em 0; padding: .2em 1em; color: {{muted}}; }
            .tablebox { overflow-x: auto; }
            table { border-collapse: collapse; margin: 1em 0; }
            th, td { border: 1px solid {{divider}}; padding: 6px 10px; text-align: left; }
            th { background: {{chrome}}; }
            img { max-width: 100%; }
            .tok-com { color: var(--tok-com); font-style: italic; }
            .tok-kw  { color: #d2a8ff; }
            .tok-str { color: #8ddb8c; }
            .tok-num { color: #79c0ff; }
            .tok-fn  { color: #ffa657; }
            .tok-lit { color: #79c0ff; }
            """;
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

    private Brush Brush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
