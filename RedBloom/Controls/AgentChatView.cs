using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Controls;

/// <summary>
/// An agent session drawn as a chat rather than as a terminal.
/// </summary>
/// <remarks>
/// A terminal can only paint characters, so a reply in it is a wall of text: no copy button on a
/// code block, no folding a thousand lines of command output away, no marking what the agent is
/// doing right now as distinct from what it said. This is the same WebView2 the terminal already
/// runs in, pointed at a page of our own instead of at xterm.js.
/// <para>
/// Everything the page renders is built as HTML on this side, by <see cref="Markdown"/>, which
/// escapes first — model output must never be able to become markup, let alone script.
/// </para>
/// </remarks>
public sealed class AgentChatView : UserControl, IAgentToolHost, IDisposable
{
    private const string VirtualHost = "redbloom.assets";
    private const string PageUrl = $"https://{VirtualHost}/chat.html";

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedBloom",
            "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private readonly AiAgent _agent;
    private readonly ChatSession _chat;
    private readonly IAgentTransport _transport;
    private readonly WebView2 _webView = new();
    private readonly List<AgentMessage> _history = [];

    /// <summary>The reply being streamed, re-rendered as it grows.</summary>
    private readonly StringBuilder _reply = new();

    private readonly DispatcherTimer _paint;

    private CancellationTokenSource? _turn;
    private TaskCompletionSource<char>? _approval;
    private string _suggested = string.Empty;
    private bool _pageReady;
    private bool _dirty;
    private bool _disposed;
    private int _activity;

    public AgentChatView(AiAgent agent, ChatSession chat)
    {
        _agent = agent;
        _chat = chat;
        _transport = AgentTransports.For(agent, this);

        // A reopened chat starts with its own past, so the model picks up where it left off
        // rather than meeting the user again.
        foreach (var saved in chat.Turns)
        {
            _history.Add(new AgentMessage(
                saved.Role == "assistant" ? AgentRole.Assistant : AgentRole.User,
                saved.Text));
        }

        ApplyWebViewBackground();
        Content = _webView;

        // Markdown is re-rendered from scratch on each repaint, so the rate is capped rather
        // than tied to how fast tokens arrive.
        _paint = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        _paint.Tick += (_, _) => PaintReply();

        Loaded += OnLoaded;
    }

    /// <summary>Re-sends the avatar, after the chat or the agent has been given a new one.</summary>
    public void RefreshAvatar() => Post(new { t = "avatar", src = AvatarDataUri() });

    /// <summary>Raised once the session ends, with a reason to show on the tab.</summary>
    public event EventHandler<string>? SessionEnded;

    // ---- page ----

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
            Content = new TextBlock
            {
                Text = $"WebView2 failed to initialize: {ex.Message}",
                Margin = new Thickness(16),
            };
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
        core.Settings.IsSwipeNavigationEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;

        // Links open in the user's browser, never in this view.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };

        core.Navigate(PageUrl);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        JsonElement message;
        try
        {
            message = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        var kind = message.TryGetProperty("t", out var t) ? t.GetString() : null;

        switch (kind)
        {
            case "ready":
                _pageReady = true;
                PushTheme();
                Greet();
                break;

            case "send" when message.TryGetProperty("text", out var text):
                Submit(text.GetString() ?? string.Empty);
                break;

            case "approve" when message.TryGetProperty("answer", out var answer):
                var choice = answer.GetString();
                _approval?.TrySetResult(choice is { Length: > 0 } ? choice[0] : 'n');
                _approval = null;
                break;
        }
    }

    private void Post(object message)
    {
        if (_pageReady || message is not null)
        {
            _webView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(message));
        }
    }

    /// <summary>
    /// Decides what the WebView paints under the page.
    /// </summary>
    /// <remarks>
    /// Without this the control keeps its opaque default, and the page's own translucent plate
    /// then sits on that instead of on the window — so a background picture or the live
    /// wallpaper never reaches the chat. Alpha may only be 0 or 255 here: a value in between
    /// throws and takes the WebView's initialisation with it, which is why the dimming is done
    /// by the page's background rather than by this colour.
    /// </remarks>
    private void ApplyWebViewBackground()
    {
        var s = ThemeService.Settings;
        var background = ThemeService.ParseColor(s.TerminalBackground, System.Windows.Media.Colors.Black);

        _webView.DefaultBackgroundColor = s.BackgroundMode == BackgroundMode.None
            ? System.Drawing.Color.FromArgb(255, background.R, background.G, background.B)
            : System.Drawing.Color.FromArgb(0, background.R, background.G, background.B);
    }

    /// <summary>Hands the page the app's own colours and fonts, so the chat matches the window.</summary>
    private void PushTheme()
    {
        var s = ThemeService.Settings;
        var tint = ThemeService.ParseColor(s.TerminalBackground, System.Windows.Media.Colors.Black);
        var raised = ThemeService.ParseColor(s.SurfaceRaised, System.Windows.Media.Colors.Black);
        var accent = ThemeService.ParseColor(s.AccentDim, System.Windows.Media.Colors.Black);

        Post(new
        {
            t = "theme",
            vars = new Dictionary<string, string>
            {
                // Transparent on purpose. The window already draws the TerminalFill plate behind
                // this view, at the same opacity the sidebar uses for its own — so a tint here
                // would be a second coat of the same dimming and the chat would come out darker
                // than every other panel.
                ["page"] = "transparent",

                // The composer keeps a plate of its own: it is a control strip, and it has to
                // stay legible over whatever the wallpaper happens to be doing under it.
                ["bar"] = $"rgba({tint.R},{tint.G},{tint.B},0.45)",

                // The agent's replies sit on a plate of their own, kept translucent so the
                // wallpaper still reads through it.
                ["bubble"] = $"rgba({raised.R},{raised.G},{raised.B},0.45)",

                // The user's plate leans on the accent so the two sides read apart even where
                // the avatar has scrolled out of view.
                ["bubble-user"] = $"rgba({accent.R},{accent.G},{accent.B},0.22)",

                // The agent's own nick colour, falling back to the theme accent.
                ["nick"] = string.IsNullOrWhiteSpace(_agent.NameColor) ? s.Accent : _agent.NameColor,
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
                ["code-size"] = $"{s.TerminalFontSize:0.#}px",
            },
        });
    }

    /// <summary>
    /// The agent's avatar, inlined as a data URI, or empty when there is none to show.
    /// </summary>
    /// <remarks>
    /// Inlined rather than linked: the page is served from a virtual host mapped to the Assets
    /// folder, and a picture the user chose lives anywhere but there. Mapping a second folder
    /// would hand the page read access to wherever that picture happens to sit, which is a lot
    /// of reach to grant for an avatar.
    /// </remarks>
    private string AvatarDataUri()
    {
        // The chat's own picture wins; the agent's is the fallback.
        var path = string.IsNullOrWhiteSpace(_chat.AvatarPath) ? _agent.AvatarPath : _chat.AvatarPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            // An avatar is a small picture; anything this size is a mistake, and inlining it
            // would bloat every message the page is sent.
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
            {
                return string.Empty;
            }

            var media = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };

            return $"data:{media};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void Greet()
    {
        Post(new { t = "avatar", src = AvatarDataUri() });

        var bits = new List<string> { _agent.Model };

        if (_agent.Provider == AiProvider.Anthropic)
        {
            bits.Add(_agent.Thinking ? $"thinking adaptive, effort {_agent.Effort}" : "thinking off");
        }

        if (_agent.AllowCommands)
        {
            bits.Add(_agent.AskBeforeRun ? "can run commands, each one asks" : "runs commands without asking");
        }

        Post(new { t = "note", html = Markdown.Escape($"{_agent.Name} — {string.Join(" · ", bits)}") });

        // Everything said before this run is redrawn, so reopening a chat looks like scrolling
        // back through it rather than starting over.
        foreach (var turn in _chat.Turns)
        {
            if (turn.Role == "assistant")
            {
                Post(new { t = "assistant", label = _agent.Name, html = Markdown.ToHtml(turn.Text) });
            }
            else
            {
                Post(new { t = "user", html = Markdown.ToHtml(turn.Text) });
            }

            Post(new { t = "endTurn" });
        }

        Post(new { t = "status", text = _agent.ResolvedBaseUrl, busy = false });
    }

    /// <summary>Writes the conversation back to the chat it belongs to.</summary>
    private void Persist()
    {
        _chat.Turns =
        [
            .. _history.Select(m => new ChatTurn
            {
                Role = m.Role == AgentRole.Assistant ? "assistant" : "user",
                Text = m.Text,
            }),
        ];

        if (_chat.Title.Length == 0 && _history.FirstOrDefault() is { Role: AgentRole.User } first)
        {
            _chat.Title = ChatSession.TitleFrom(first.Text);
        }

        _chat.Touch();
        ChatStore.Save(_chat);
    }

    // ---- a turn ----

    private void Submit(string text)
    {
        var question = text.Trim();

        if (question.Length == 0 || _turn is not null)
        {
            return;
        }

        Post(new { t = "user", html = Markdown.ToHtml(question) });
        _history.Add(new AgentMessage(AgentRole.User, question));
        _ = RunTurnAsync();
    }

    private async Task RunTurnAsync()
    {
        var turn = new CancellationTokenSource();
        _turn = turn;

        _reply.Clear();
        Post(new { t = "status", text = "working…", busy = true });

        try
        {
            await foreach (var item in _transport.SendAsync(_history, turn.Token).ConfigureAwait(true))
            {
                Handle(item);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Post(new { t = "note", html = Markdown.Escape(ex.Message) });
        }
        finally
        {
            _paint.Stop();
            PaintReply();
            Post(new { t = "endTurn" });
            Post(new { t = "status", text = _agent.ResolvedBaseUrl, busy = false });

            _turn = null;
            turn.Dispose();
        }

        if (_reply.Length > 0)
        {
            _history.Add(new AgentMessage(AgentRole.Assistant, _reply.ToString()));
        }
        else
        {
            // Nothing came back, so the question is dropped too rather than being re-sent as
            // unanswered history on the next turn.
            _history.RemoveAt(_history.Count - 1);
        }

        Persist();
    }

    private void Handle(AgentEvent item)
    {
        switch (item.Kind)
        {
            case AgentEventKind.Thinking:
                Post(new { t = "status", text = "thinking…", busy = true });
                break;

            case AgentEventKind.Text:
                _reply.Append(item.Text);
                _dirty = true;
                _paint.Start();
                break;

            case AgentEventKind.ToolCall:
                // The reply so far is committed before the command, so the two do not interleave.
                _paint.Stop();
                PaintReply();
                _reply.Clear();
                Post(new { t = "endTurn" });

                _activity++;
                var (label, what) = Describe(item.Text);
                Post(new { t = "activity", id = _activity.ToString(), state = "running", label, what });
                break;

            case AgentEventKind.ToolResult:
                Post(new
                {
                    t = "activityDone",
                    id = _activity.ToString(),
                    summary = Summarise(item.Text),
                    output = item.Text,
                });
                break;

            case AgentEventKind.ToolRefused:
                Post(new { t = "activityDone", id = _activity.ToString(), summary = "skipped", output = "" });
                break;

            case AgentEventKind.Failed:
                Post(new { t = "note", html = Markdown.Escape(item.Text) });
                break;

            case AgentEventKind.Completed:
                if (item.Text.Length > 0)
                {
                    Post(new { t = "note", html = Markdown.Escape(item.Text) });
                }

                break;
        }
    }

    private void PaintReply()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        Post(new { t = "assistant", label = _agent.Name, html = Markdown.ToHtml(_reply.ToString()) });
    }

    // ---- commands ----

    /// <inheritdoc />
    public bool Enabled => _agent.AllowCommands;

    /// <inheritdoc />
    public Task<bool> ApproveAsync(string command, CancellationToken cancellationToken)
    {
        if (!_agent.AskBeforeRun || _agent.IsAlwaysAllowed(command))
        {
            return Task.FromResult(true);
        }

        _suggested = AiAgent.SuggestAllowPattern(command);
        var pending = new TaskCompletionSource<char>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approval = pending;

        Post(new
        {
            t = "ask",
            commandHtml = Markdown.Escape(command),
            pattern = _suggested,
        });

        cancellationToken.Register(() => pending.TrySetResult('n'));

        return Continue(pending.Task);

        async Task<bool> Continue(Task<char> answer)
        {
            var choice = await answer.ConfigureAwait(true);

            if (choice is 'a' or 'A' && _suggested.Length > 0)
            {
                Remember(_suggested);
                Post(new { t = "note", html = Markdown.Escape($"“{_suggested}” will run without asking from now on") });
            }

            return choice is 'y' or 'Y' or 'a' or 'A';
        }
    }

    /// <inheritdoc />
    public Task<string> RunAsync(string command, CancellationToken cancellationToken) =>
        CommandRunner.RunAsync(command, cancellationToken);

    /// <summary>Adds a standing allowance to this session and to the saved agent behind it.</summary>
    private void Remember(string pattern)
    {
        if (!_agent.AllowedCommands.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            _agent.AllowedCommands.Add(pattern);
        }

        var saved = ThemeService.Settings.Agents.FirstOrDefault(a => a.Id == _agent.Id);

        if (saved is not null
            && !saved.AllowedCommands.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            saved.AllowedCommands.Add(pattern);
            ThemeService.Save();
        }
    }

    /// <summary>
    /// Turns a command into something worth reading above it — "reading Program.cs" rather than
    /// the raw <c>type</c> invocation.
    /// </summary>
    /// <remarks>
    /// A guess, and shown alongside the command rather than instead of it, so a wrong label
    /// costs nothing: the exact command is always on screen next to it.
    /// </remarks>
    private static (string Label, string What) Describe(string command)
    {
        var trimmed = command.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var program = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;

        return program switch
        {
            "type" or "cat" or "more" => ("reading", rest),
            "dir" or "ls" or "tree" => ("listing", rest.Length > 0 ? rest : "."),
            "findstr" or "grep" or "rg" or "sls" => ("searching", rest),
            "cd" or "pushd" => ("moving to", rest),
            "del" or "erase" or "rm" or "rmdir" => ("deleting", rest),
            "copy" or "xcopy" or "robocopy" or "move" => ("copying", rest),
            "mkdir" or "md" => ("creating", rest),
            "git" => ("git", rest),
            "dotnet" or "npm" or "npx" or "pnpm" or "yarn" or "cargo" or "go" or "pip" =>
                (program, rest),
            _ => ("running", trimmed),
        };
    }

    private static string Summarise(string output)
    {
        if (output.Length == 0)
        {
            return "no output";
        }

        var lines = output.Split('\n').Length;
        return lines == 1 ? "output (1 line)" : $"output ({lines} lines)";
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri)
            {
                UseShellExecute = true,
            });
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
        _paint.Stop();
        _turn?.Cancel();
        _approval?.TrySetResult('n');
        _transport.Dispose();
        SessionEnded?.Invoke(this, "The agent session was closed.");
        _webView.Dispose();
    }
}
