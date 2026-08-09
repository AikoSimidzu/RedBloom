using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Services;
using RedBloom.Terminal;

namespace RedBloom.Controls;

/// <summary>
/// A terminal surface: xterm.js inside WebView2, wired to an <see cref="ITerminalBackend"/>.
/// </summary>
public sealed class TerminalView : UserControl, IDisposable
{
    private const string VirtualHost = "redbloom.assets";
    private const string PageUrl = $"https://{VirtualHost}/terminal.html";

    /// <summary>WebView2 chokes on very large single messages, so posts are chunked.</summary>
    private const int MaxChunkLength = 128 * 1024;

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedBloom",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
    });

    private readonly Func<int, int, CancellationToken, Task<ITerminalBackend>> _connectAsync;
    private readonly WebView2 _webView = new();
    private readonly StringBuilder _pendingOutput = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly CancellationTokenSource _lifetime = new();

    private const int MaxReconnectAttempts = 5;

    /// <summary>A session that lasted at least this long is treated as having been healthy.</summary>
    private static readonly TimeSpan HealthySessionThreshold = TimeSpan.FromSeconds(60);

    private ITerminalBackend? _backend;
    private int _columns = 80;
    private int _rows = 24;
    private int _reconnectAttempt;
    private bool _reconnectScheduled;
    /// <summary>When the current session came up, or null once that has been accounted for.</summary>
    private DateTime? _connectedAt;
    private bool _pageReady;
    private bool _disposed;

    public TerminalView(Func<int, int, CancellationToken, Task<ITerminalBackend>> connectAsync)
    {
        _connectAsync = connectAsync;

        _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Content = _webView;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _flushTimer.Tick += (_, _) => FlushOutput();

        Loaded += OnLoaded;
    }

    /// <summary>Raised when the far end sets the window title (OSC 0/2).</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Raised once when the session ends, with a human-readable reason.</summary>
    public event EventHandler<string>? SessionEnded;

    /// <summary>Raised whenever a session comes up, including after an automatic reconnect.</summary>
    public event EventHandler? SessionStarted;

    /// <summary>
    /// A tab-management shortcut the page intercepted on our behalf, since a focused
    /// WebView2 never lets those keys reach WPF. 'T' new, 'W' close, 'N'/'P' next/previous.
    /// </summary>
    public event EventHandler<char>? AcceleratorPressed;

    public bool IsConnected => _backend?.IsRunning == true;

    /// <summary>
    /// The live SSH connection behind this terminal, or null for a local shell or a session
    /// that is not up. A split can open another shell on it without logging in again.
    /// </summary>
    public SshConnection? SshConnection => (_backend as SshBackend)?.Connection;

    /// <summary>Reopen the session by itself when the far end drops it.</summary>
    public bool AutoReconnect { get; set; }

    public void FocusTerminal()
    {
        if (_pageReady)
        {
            Post("f");
        }

        _webView.Focus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var environment = await SharedEnvironment.Value.ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowStartupFailure($"WebView2 failed to initialize: {ex.Message}");
            return;
        }

        if (_disposed)
        {
            return;
        }

        var core = _webView.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += OnNewWindowRequested;

        core.Navigate(PageUrl);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var kind = message[0];
        var body = message[1..];

        switch (kind)
        {
            case 'i':
                _backend?.Write(body);
                break;

            case 'r':
                OnPageResize(body);
                break;

            case 't':
                TitleChanged?.Invoke(this, body);
                break;

            case 'R':
                OnPageReady();
                break;

            case 'p':
                PasteFromClipboard();
                break;

            case 'y':
                CopyToClipboard(body);
                break;

            case 'l':
                OpenExternally(body);
                break;

            case 'k':
                if (body.Length == 1)
                {
                    AcceleratorPressed?.Invoke(this, body[0]);
                }

                break;
        }
    }

    private void OnPageResize(string body)
    {
        var separator = body.IndexOf(',');
        if (separator <= 0
            || !int.TryParse(body[..separator], out var columns)
            || !int.TryParse(body[(separator + 1)..], out var rows))
        {
            return;
        }

        _columns = columns;
        _rows = rows;
        _backend?.Resize(columns, rows);
    }

    private async void OnPageReady()
    {
        if (_pageReady)
        {
            return;
        }

        _pageReady = true;
        _flushTimer.Start();

        PushAppearance();
        ThemeService.Applied += PushAppearance;

        await ConnectAsync().ConfigureAwait(true);
    }

    /// <summary>Sends the current appearance settings to the page.</summary>
    private void PushAppearance()
    {
        if (_disposed || !_pageReady)
        {
            return;
        }

        var s = ThemeService.Settings;
        var payload = JsonSerializer.Serialize(new
        {
            fontFamily = s.TerminalFontFamily,
            fontSize = s.TerminalFontSize,
            lineHeight = s.TerminalLineHeight,
            cursorStyle = s.CursorStyle,
            cursorBlink = s.CursorBlink,
            scrollback = s.Scrollback,
            theme = new
            {
                // The page stays fully transparent: the terminal's colour and how much shows
                // through it are painted by WPF underneath, which is also what lets a
                // background picture sit behind the text.
                background = "rgba(0,0,0,0)",
                foreground = s.TerminalForeground,
                cursor = s.TerminalCursor,
                cursorAccent = s.TerminalBackground,
                selectionBackground = s.TerminalSelection,
                black = s.AnsiBlack,
                red = s.AnsiRed,
                green = s.AnsiGreen,
                yellow = s.AnsiYellow,
                blue = s.AnsiBlue,
                magenta = s.AnsiMagenta,
                cyan = s.AnsiCyan,
                white = s.AnsiWhite,
                brightBlack = s.AnsiBrightBlack,
                brightRed = s.AnsiBrightRed,
                brightGreen = s.AnsiBrightGreen,
                brightYellow = s.AnsiBrightYellow,
                brightBlue = s.AnsiBrightBlue,
                brightMagenta = s.AnsiBrightMagenta,
                brightCyan = s.AnsiBrightCyan,
                brightWhite = s.AnsiBrightWhite,
            },
        });

        Post("S" + payload);
    }

    private async Task ConnectAsync()
    {
        _reconnectScheduled = false;

        ITerminalBackend backend;
        try
        {
            backend = await _connectAsync(_columns, _rows, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
            return;
        }

        if (_disposed)
        {
            backend.Dispose();
            return;
        }

        _backend = backend;
        backend.Output += OnBackendOutput;
        backend.Closed += OnBackendClosed;

        try
        {
            await backend.StartAsync(_columns, _rows, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
            return;
        }

        _connectedAt = DateTime.UtcNow;
        SessionStarted?.Invoke(this, EventArgs.Empty);
        Post("f");
    }

    private void ReportFailure(string message)
    {
        Post("b" + Sanitize(message));
        SessionEnded?.Invoke(this, message);
        TryScheduleReconnect();
    }

    /// <summary>
    /// Reopens the session after an unexpected drop.
    /// </summary>
    /// <remarks>
    /// There is no way to tell an idle timeout apart from the user typing "exit" — both look
    /// like the far end closing the channel — so this backs off and gives up after a handful
    /// of tries rather than reconnecting forever into a shell someone deliberately left.
    /// </remarks>
    private async void TryScheduleReconnect()
    {
        if (!AutoReconnect || _disposed || _reconnectScheduled)
        {
            return;
        }

        // A session that stayed up counts as healthy, so a later drop starts from scratch.
        // Consumed here and cleared: without that, a connection that never comes up at all
        // would keep looking "healthy" as time passed and retry forever at the first delay.
        if (_connectedAt is { } since)
        {
            if (DateTime.UtcNow - since > HealthySessionThreshold)
            {
                _reconnectAttempt = 0;
            }

            _connectedAt = null;
        }

        if (_reconnectAttempt >= MaxReconnectAttempts)
        {
            Post("b" + $"Gave up reconnecting after {MaxReconnectAttempts} attempts.");
            return;
        }

        _reconnectScheduled = true;
        _reconnectAttempt++;

        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _reconnectAttempt)));
        Post("b" + $"Reconnecting in {delay.TotalSeconds:0}s "
                 + $"(attempt {_reconnectAttempt} of {MaxReconnectAttempts})…");

        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        DetachBackend();
        await ConnectAsync().ConfigureAwait(true);
    }

    private void DetachBackend()
    {
        if (_backend is null)
        {
            return;
        }

        _backend.Output -= OnBackendOutput;
        _backend.Closed -= OnBackendClosed;
        _backend.Dispose();
        _backend = null;
    }

    private void OnBackendOutput(string data)
    {
        lock (_pendingOutput)
        {
            _pendingOutput.Append(data);
        }
    }

    private void OnBackendClosed(string reason)
    {
        // Backends raise this from their own threads.
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            FlushOutput();
            Post("b" + Sanitize(reason));
            SessionEnded?.Invoke(this, reason);
            TryScheduleReconnect();
        });
    }

    private void FlushOutput()
    {
        if (_disposed || !_pageReady)
        {
            return;
        }

        string batch;
        lock (_pendingOutput)
        {
            if (_pendingOutput.Length == 0)
            {
                return;
            }

            batch = _pendingOutput.ToString();
            _pendingOutput.Clear();
        }

        for (var offset = 0; offset < batch.Length; offset += MaxChunkLength)
        {
            var length = Math.Min(MaxChunkLength, batch.Length - offset);
            Post("o" + batch.Substring(offset, length));
        }
    }

    private void PasteFromClipboard()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            // Another process is holding the clipboard open.
            return;
        }

        if (text.Length > 0)
        {
            // Terminals expect CR, not CRLF, for a submitted line.
            _backend?.Write(text.ReplaceLineEndings("\r"));
        }
    }

    /// <summary>
    /// Puts the selection on the clipboard, retrying briefly.
    /// </summary>
    /// <remarks>
    /// The Win32 clipboard is a single shared resource that any process can hold open, so
    /// a write failing once is routine and means nothing is wrong. Silently swallowing that
    /// is what makes a copy look like it simply did not happen, hence the retries and the
    /// visible notice once they are exhausted.
    /// </remarks>
    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                // copy: true leaves the text on the clipboard after this process exits.
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException)
            {
                Thread.Sleep(30);
            }
        }

        Post("b" + "Could not copy: another program is holding the clipboard open.");
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return;
        }

        // Only hand the shell schemes a browser will treat as navigable.
        if (parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not open {uri}: {ex.Message}");
        }
    }

    private void ShowStartupFailure(string message)
    {
        Content = new TextBlock
        {
            Text = message,
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.IndianRed,
        };
        SessionEnded?.Invoke(this, message);
    }

    private void Post(string message)
    {
        if (_disposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            _webView.CoreWebView2.PostWebMessageAsString(message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The control was torn down between the check and the post.
        }
    }

    /// <summary>Strips control characters so a host notice cannot inject escape sequences.</summary>
    private static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Stop();
        ThemeService.Applied -= PushAppearance;

        _lifetime.Cancel();
        _lifetime.Dispose();

        if (_backend is not null)
        {
            _backend.Output -= OnBackendOutput;
            _backend.Closed -= OnBackendClosed;
            _backend.Dispose();
            _backend = null;
        }

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
        }

        _webView.Dispose();
    }
}


