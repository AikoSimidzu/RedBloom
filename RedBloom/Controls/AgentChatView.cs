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
    private readonly List<ChatTurn> _history = [];

    /// <summary>Attached in the composer and not yet sent.</summary>
    private readonly List<string> _pending = [];

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

        // A chat that was switched to another model keeps it. Only the name is overridden; the
        // endpoint, key and permissions stay the agent's.
        if (!string.IsNullOrWhiteSpace(chat.Model))
        {
            _agent.Model = chat.Model;
        }

        _transport = AgentTransports.For(agent, this);

        // A reopened chat starts with its own past, so the model picks up where it left off
        // rather than meeting the user again.
        _history.AddRange(chat.Turns);

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

    private void AttachFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Attach(dialog.FileNames);
        }
    }

    private void AttachFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Attach([dialog.FolderName]);
        }
    }

    private void Attach(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!_pending.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _pending.Add(path);
            }
        }

        PushPending();
    }

    /// <summary>Shows what is pinned to the composer but not yet sent.</summary>
    private void PushPending() =>
        Post(new { t = "pending", files = _pending.Select(Attachments.Describe) });

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
                _ = PushModelsAsync();
                break;

            case "model" when message.TryGetProperty("name", out var picked):
                SwitchModel(picked.GetString() ?? string.Empty);
                break;

            case "send" when message.TryGetProperty("text", out var text):
                Submit(text.GetString() ?? string.Empty);
                break;

            case "attach":
                AttachFiles();
                break;

            case "attachFolder":
                AttachFolder();
                break;

            case "detach" when message.TryGetProperty("path", out var drop):
                _pending.Remove(drop.GetString() ?? string.Empty);
                PushPending();
                break;

            case "openAttachment" when message.TryGetProperty("path", out var target):
                var path = target.GetString() ?? string.Empty;

                if (message.TryGetProperty("how", out var how) && how.GetString() == "reveal")
                {
                    Attachments.Reveal(path);
                }
                else
                {
                    Attachments.Open(path);
                }

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

    /// <summary>
    /// Fills the model picker, asking the endpoint what it serves.
    /// </summary>
    /// <remarks>
    /// The picker is shown as soon as the current model is known and filled in again when the
    /// listing arrives, so a slow or silent endpoint leaves a one-item list rather than an empty
    /// control that looks broken.
    /// </remarks>
    private async Task PushModelsAsync()
    {
        Post(new { t = "models", list = Array.Empty<string>(), current = _agent.Model });

        var models = await ModelCatalog.FetchAsync(_agent).ConfigureAwait(true);

        if (!_disposed && models.Count > 0)
        {
            Post(new { t = "models", list = models, current = _agent.Model });
        }
    }

    /// <summary>
    /// Points this chat at another model, from the next message on.
    /// </summary>
    /// <remarks>
    /// Only the model changes: the endpoint, the key and the tools are the agent's, and the
    /// transport reads the name afresh on every request, so nothing has to be torn down and the
    /// conversation carries over intact. A model swapped mid-chat therefore answers with the
    /// whole history behind it, which is the point — the reason to reach for a bigger one is
    /// usually the question that was just asked.
    /// </remarks>
    private void SwitchModel(string name)
    {
        name = name.Trim();

        if (name.Length == 0 || name == _agent.Model)
        {
            return;
        }

        _agent.Model = name;
        _chat.Model = name;
        ChatStore.Save(_chat);

        Post(new { t = "status", text = $"answering with {name}", busy = false });
        _ = PushModelsAsync();
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
                Post(new
                {
                    t = "user",
                    html = Markdown.ToHtml(turn.Text),
                    files = turn.Attachments.Select(Attachments.Describe),
                });
            }

            Post(new { t = "endTurn" });
        }

        Post(new { t = "status", text = _agent.ResolvedBaseUrl, busy = false });
        PushContext();
    }

    /// <summary>Tells the page how full the context is, and lists the attachments.</summary>
    private void PushContext()
    {
        var used = UsedTokens();

        Post(new { t = "context", used, window = _agent.ContextWindow });
    }

    /// <summary>
    /// Folds the older part of the conversation into a summary when it is filling the window.
    /// </summary>
    /// <remarks>
    /// Done here rather than by the endpoint: the server-side feature exists only on Anthropic's
    /// own API, and half the agents here point at something else. The summary is asked for
    /// through a transport with no tools bound, so summarising can never run a command.
    /// The last few turns are kept verbatim — those are the ones the next question is usually
    /// about, and a summary of them would lose exactly the detail still in play.
    /// </remarks>
    private async Task CompactIfFullAsync(CancellationToken cancellationToken)
    {
        const int KeepVerbatim = 6;

        var used = UsedTokens();

        if (used < _agent.ContextWindow * 0.7 || _history.Count <= KeepVerbatim + 2)
        {
            return;
        }

        Post(new { t = "status", text = "summarising the earlier part of the chat…", busy = true });

        // Keep the tail, and make sure it starts on a question: both APIs expect the roles to
        // alternate, and a tail beginning with a reply would sit next to the summary's own.
        var start = _history.Count - KeepVerbatim;

        while (start < _history.Count && _history[start].Role != "user")
        {
            start++;
        }

        if (start >= _history.Count)
        {
            return;
        }

        var transcript = new StringBuilder();

        for (var i = 0; i < start; i++)
        {
            transcript.Append(_history[i].Role == "user" ? "User: " : "Assistant: ")
                .AppendLine(_history[i].Text);
        }

        var summary = new StringBuilder();

        try
        {
            using var plain = AgentTransports.For(_agent);

            var ask = new List<AgentMessage>
            {
                new(AgentRole.User,
                    "Summarise the conversation below so it can stand in for the full text. Keep "
                    + "decisions, facts, names, paths and anything still unresolved; drop "
                    + "pleasantries. Write it as notes, not as a reply to me.\n\n"
                    + transcript),
            };

            await foreach (var item in plain.SendAsync(ask, cancellationToken).ConfigureAwait(true))
            {
                if (item.Kind == AgentEventKind.Text)
                {
                    summary.Append(item.Text);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed summary is not worth failing the turn over — the conversation simply
            // goes out at full length and the endpoint decides what to do about it.
            Post(new { t = "note", html = Markdown.Escape($"Could not compact the chat: {ex.Message}") });
            return;
        }

        if (summary.Length == 0)
        {
            return;
        }

        var tail = _history.Skip(start).ToList();
        _history.Clear();
        _history.Add(new ChatTurn { Role = "user", Text = "Here is where we had got to:\n\n" + summary });
        _history.Add(new ChatTurn { Role = "assistant", Text = "Understood — carrying on from there." });
        _history.AddRange(tail);

        Post(new
        {
            t = "note",
            html = Markdown.Escape("The earlier part of this chat was summarised to make room."),
        });

        Persist();
    }

    /// <summary>Writes the conversation back to the chat it belongs to.</summary>
    private void Persist()
    {
        _chat.Turns = [.. _history];

        if (_chat.Title.Length == 0 && _history.FirstOrDefault() is { Role: "user" } first)
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

        // The attachments travel with the message they were pinned for, and the composer is
        // cleared: a pin that stayed put would silently re-send the file on every later turn.
        var turn = new ChatTurn { Role = "user", Text = question, Attachments = [.. _pending] };
        _pending.Clear();

        Post(new
        {
            t = "user",
            html = Markdown.ToHtml(question),
            files = turn.Attachments.Select(Attachments.Describe),
        });

        _history.Add(turn);
        PushPending();
        _ = RunTurnAsync();
    }

    /// <summary>
    /// What actually goes to the model: the attachments, then the conversation.
    /// </summary>
    /// <remarks>
    /// The attachment block is a leading turn built fresh each time rather than something kept
    /// in the history, so editing an attached file between questions changes what the model
    /// sees, and the saved chat stays a record of what was said rather than of what was read.
    /// </remarks>
    /// <param name="pictures">
    /// False when the result is only going to be measured. Encoding the attached images is the
    /// expensive part of building this, and the estimate charges a flat rate per picture anyway.
    /// </param>
    private List<AgentMessage> Conversation(bool pictures = true)
    {
        var conversation = new List<AgentMessage>(_history.Count);

        foreach (var turn in _history)
        {
            var role = turn.Role == "assistant" ? AgentRole.Assistant : AgentRole.User;
            var context = ChatContext.Build(turn.Attachments);

            conversation.Add(new AgentMessage(
                role,
                context is null ? turn.Text : turn.Text + "\n\n" + context,
                pictures ? ChatContext.Images(turn.Attachments) : null));
        }

        return conversation;
    }

    /// <summary>Roughly how much of the model's window this conversation now takes up.</summary>
    private int UsedTokens() =>
        ChatContext.EstimateTokens(Conversation(pictures: false).Select(m => m.Text))
        + (_history.Sum(turn => ChatContext.CountImages(turn.Attachments)) * ChatContext.TokensPerImage);

    private async Task RunTurnAsync()
    {
        var turn = new CancellationTokenSource();
        _turn = turn;

        _reply.Clear();
        Post(new { t = "status", text = "working…", busy = true });
        Post(new { t = "thinking", on = true, label = _agent.Name });

        try
        {
            await CompactIfFullAsync(turn.Token).ConfigureAwait(true);

            await foreach (var item in _transport.SendAsync(Conversation(), turn.Token).ConfigureAwait(true))
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
            _history.Add(new ChatTurn { Role = "assistant", Text = _reply.ToString() });
        }
        else
        {
            // Nothing came back, so the question is dropped too rather than being re-sent as
            // unanswered history on the next turn.
            _history.RemoveAt(_history.Count - 1);
        }

        Persist();
        PushContext();
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
    /// <remarks>
    /// A command asking for administrator rights is always put to the user, standing allowance or
    /// not. An allowance is granted for a pattern in the ordinary run of things; it is not consent
    /// to run that same pattern one privilege level up.
    /// </remarks>
    public Task<bool> ApproveAsync(string command, bool elevated, CancellationToken cancellationToken)
    {
        if (!elevated && (!_agent.AskBeforeRun || _agent.IsAlwaysAllowed(command)))
        {
            return Task.FromResult(true);
        }

        _suggested = elevated ? string.Empty : AiAgent.SuggestAllowPattern(command);
        var pending = new TaskCompletionSource<char>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approval = pending;

        Post(new
        {
            t = "ask",
            commandHtml = Markdown.Escape(command),
            pattern = _suggested,
            elevated,
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
    /// <remarks>
    /// An elevated command goes to the helper, starting it — and so asking Windows for consent —
    /// the first time one is approved. Consent refused is reported back to the model as the
    /// command's result rather than as a failed turn, so it can say what it would have done or
    /// find a way that needs no administrator at all.
    /// </remarks>
    public async Task<string> RunAsync(string command, bool elevated, CancellationToken cancellationToken)
    {
        if (!elevated)
        {
            return await CommandRunner.RunAsync(command, cancellationToken).ConfigureAwait(true);
        }

        if (!ElevatedHost.IsRunning
            && await ElevatedHost.StartAsync(cancellationToken).ConfigureAwait(true) is { } refused)
        {
            return $"This command needed administrator rights and did not get them: {refused}";
        }

        return await ElevatedHost.RunAsync(command, cancellationToken).ConfigureAwait(true);
    }

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
