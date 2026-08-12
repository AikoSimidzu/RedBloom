using System.Globalization;
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

    /// <summary>Files the agent has handed over during the turn now running.</summary>
    private readonly List<string> _shared = [];

    /// <summary>The reply being streamed, re-rendered as it grows.</summary>
    private readonly StringBuilder _reply = new();

    /// <summary>The model's reasoning for this turn, where the endpoint returns any.</summary>
    private readonly StringBuilder _thinking = new();

    private readonly DispatcherTimer _paint;

    private CancellationTokenSource? _turn;
    private TaskCompletionSource<char>? _approval;
    private string _suggested = string.Empty;
    private bool _pageReady;
    private bool _dirty;
    private bool _disposed;
    private int _activity;

    /// <summary>What this turn has cost, and whether those figures came from the endpoint.</summary>
    private int _spentIn;
    private int _spentOut;
    private bool _counted;

    /// <summary>
    /// What the agent is called in this chat. A chat may name it something of its own; empty
    /// falls back to the agent's name, the same way the avatar and the model do.
    /// </summary>
    private string BotName =>
        string.IsNullOrWhiteSpace(_chat.BotName) ? _agent.Name : _chat.BotName;

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

    /// <summary>
    /// Renames the agent everywhere it is named on the page, after a chat has renamed it.
    /// </summary>
    /// <remarks>
    /// Past turns are relabelled too rather than only the next one: the name is how this chat
    /// refers to the agent, not a record of what it happened to be called at the time.
    /// </remarks>
    public void RefreshBotName() => Post(new { t = "rename", name = BotName });

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

    /// <summary>
    /// Attaches a saved SSH connection, so the agent can write commands for a machine the user
    /// already has set up.
    /// </summary>
    /// <remarks>
    /// The session is attached by id rather than by its details: what goes to the model is built
    /// fresh on every send, so editing the connection between turns changes what the agent sees,
    /// and the saved chat never holds a copy of the host and account.
    /// </remarks>
    private void AttachSession()
    {
        var sessions = SessionCatalog.All;

        if (sessions.Count == 0)
        {
            Post(new { t = "note", html = Markdown.Escape("There are no saved SSH connections to attach.") });
            return;
        }

        var picker = new Views.SessionPickerDialog(sessions) { Owner = Window.GetWindow(this) };

        if (picker.ShowDialog() == true && picker.Chosen is { } chosen)
        {
            Attach([SessionCatalog.Reference(chosen, picker.SendsSecret)]);
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
                PushStrings();
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

            case "regenerate":
                Regenerate();
                break;

            case "command" when message.TryGetProperty("name", out var command):
                RunCommand(command.GetString() ?? string.Empty);
                break;

            case "web" when message.TryGetProperty("query", out var query):
                OpenWebSearch(query.GetString() ?? string.Empty);
                break;

            case "askOther" when message.TryGetProperty("text", out var pick):
                AskOtherAgent(pick.GetString() ?? string.Empty);
                break;

            case "feedback" when message.TryGetProperty("verdict", out var verdict):
                SaveFeedback(
                    verdict.GetString() ?? string.Empty,
                    message.TryGetProperty("note", out var note) ? note.GetString() ?? string.Empty : string.Empty,
                    message.TryGetProperty("text", out var reply) ? reply.GetString() ?? string.Empty : string.Empty);
                break;

            case "attach":
                AttachFiles();
                break;

            case "attachFolder":
                AttachFolder();
                break;

            case "attachSsh":
                AttachSession();
                break;

            case "stop":
                // Whatever has already arrived is kept: the cancelled turn is committed to the
                // history as far as it got, so a long answer stopped halfway is still there.
                _turn?.Cancel();
                _approval?.TrySetResult('n');
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
        // Told to go away outright, rather than merely not being filled: saying nothing relies
        // on the picker never having been shown, and one stray message would leave it stranded
        // on a chat whose model cannot be changed at all.
        if (!_agent.CanChooseModel)
        {
            Post(new { t = "models", hide = true });

            return;
        }

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

        // Refused rather than trusted: the picker is not offered to these agents, so a message
        // asking to change their model did not come from anything this app put on screen.
        if (!_agent.CanChooseModel || name.Length == 0 || name == _agent.Model)
        {
            return;
        }

        _agent.Model = name;
        _chat.Model = name;
        ChatStore.Save(_chat);

        Post(new { t = "status", text = $"answering with {name}", busy = false });
        _ = PushModelsAsync();
    }

    /// <summary>
    /// Hands the page every word it shows, in the window's language.
    /// </summary>
    /// <remarks>
    /// The page carries English defaults so it is readable even if this never arrives, but the
    /// wording lives here with the rest of the strings rather than being duplicated in HTML where
    /// switching language could not reach it.
    /// </remarks>
    private void PushStrings()
    {
        string[] keys =
        [
            "L_ChatAsk", "L_ChatSend", "L_ChatAttachFile", "L_ChatAttachFolder", "L_ChatAttachSsh",
            "L_ChatRemove", "L_ChatOpenFile", "L_ChatOpenFolder", "L_ChatReveal",
            "L_ChatRun", "L_ChatSkip", "L_ChatAlways", "L_ChatAlwaysNote", "L_ChatAdminWarn",
            "L_ChatModelTitle", "L_ChatModelOther", "L_ChatModelPlaceholder",
            "L_ChatContextTip", "L_ChatSpentCounted", "L_ChatSpentEstimated", "L_ChatCopied",
            "L_ChatReasoning", "L_ChatStop",
            "L_ChatCopy", "L_ChatRepeat", "L_ChatEdit", "L_ChatLike", "L_ChatDislike",
            "L_ChatRegenerate", "L_ChatDislikeAsk", "L_ChatDislikeSend", "L_ChatThink",
            "L_ChatDownload", "L_ChatCopyImage", "L_ChatOpenExternal", "L_ChatClose",
            "L_ChatPrev", "L_ChatNext",
            "L_ChatCmdCompact", "L_ChatCmdCompactHint", "L_ChatCmdRetry", "L_ChatCmdRetryHint",
            "L_ChatCtxCopy", "L_ChatCtxWeb", "L_ChatCtxAsk", "L_ChatCtxAskOther",
            "L_ChatRpAct", "L_ChatRpState", "L_ChatRpStatus", "L_ChatRpAttempt",
            "L_ChatRpMe", "L_ChatRpAgent", "L_ChatRpActPrompt",
            "L_ChatRpStatePrompt", "L_ChatRpStatusPrompt", "L_ChatRpAttemptOk", "L_ChatRpAttemptFail",
            "L_ChatRpStateWord", "L_ChatRpStatusWord", "L_ChatRpAttemptWord", "L_ChatRpSuccess", "L_ChatRpFail",
        ];

        Post(new
        {
            t = "strings",
            s = keys.ToDictionary(key => key[6..], LocalizationService.T),
        });
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

        // The slash commands this chat answers to. A room reuses the same page but offers none of
        // these, so the set is declared per surface rather than baked into the page.
        Post(new { t = "commands", names = new[] { "compact", "retry" } });

        // The roleplay quick actions are for a character, not an assistant, so they show only
        // when this agent is one.
        Post(new { t = "rp", on = _agent.IsRoleplay });

        var bits = new List<string> { _agent.Model };

        if (_agent.Provider == AiProvider.Anthropic)
        {
            bits.Add(_agent.Thinking ? $"thinking adaptive, effort {_agent.Effort}" : "thinking off");
        }

        if (_agent.AllowCommands)
        {
            bits.Add(_agent.AskBeforeRun ? "can run commands, each one asks" : "runs commands without asking");
        }

        if (_agent.AllowImages)
        {
            bits.Add("can draw pictures");
        }

        if (_agent.AllowAgents)
        {
            bits.Add("can call other agents");
        }

        Post(new { t = "note", html = Markdown.Escape($"{BotName} — {string.Join(" · ", bits)}") });

        // Everything said before this run is redrawn, so reopening a chat looks like scrolling
        // back through it rather than starting over.
        foreach (var turn in _chat.Turns)
        {
            if (turn.Role == "assistant")
            {
                Post(new { t = "assistant", label = BotName, html = Markdown.ToHtml(turn.Text) });

                // Files the agent handed over are part of what it said, so they come back with it.
                if (turn.Attachments.Count > 0)
                {
                    Post(new
                    {
                        t = "shared",
                        label = BotName,
                        note = string.Empty,
                        files = turn.Attachments.Select(Attachments.Describe),
                    });
                }
            }
            else
            {
                Post(new
                {
                    t = "user",
                    html = Markdown.ToHtml(turn.Text),
                    text = turn.Text,
                    files = turn.Attachments.Select(Attachments.Describe),
                });
            }

            Post(new { t = "endTurn" });
        }

        Post(new { t = "status", text = _agent.Origin, busy = false });
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
        // An image agent has no conversation to compact — each message is a self-contained prompt,
        // and asking it to "summarise" would only set it drawing a picture of the instruction.
        if (_agent.Provider == AiProvider.ImageGen)
        {
            return;
        }

        var used = UsedTokens();

        if (used < _agent.ContextWindow * 0.7)
        {
            return;
        }

        await CompactAsync(force: false, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs a slash command typed in the composer. The set is small and each one maps to a thing
    /// the chat can already do, reached by name rather than by hunting for a button.
    /// </summary>
    private void RunCommand(string name)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "compact":
                _ = CompactNowAsync();
                break;

            case "retry":
            case "regenerate":
                Regenerate();
                break;
        }
    }

    /// <summary>
    /// Folds the earlier part of the chat into a summary on the user's say-so, whatever the window
    /// is at. The automatic pass waits until the context is filling up; this is the same fold asked
    /// for early, to clear room before it becomes pressing or to draw a line under a finished topic.
    /// </summary>
    private async Task CompactNowAsync()
    {
        if (_turn is not null)
        {
            return;
        }

        var work = new CancellationTokenSource();
        _turn = work;

        try
        {
            await CompactAsync(force: true, work.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user; the conversation is left as it was.
        }
        finally
        {
            Post(new { t = "status", text = _agent.Origin, busy = false });
            _turn = null;
            work.Dispose();
        }

        PushContext();
    }

    private async Task CompactAsync(bool force, CancellationToken cancellationToken)
    {
        const int KeepVerbatim = 6;

        if (force && _agent.Provider == AiProvider.ImageGen)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatCompactNothing")) });
            return;
        }

        // Too little to fold: the tail alone is most of the chat, and there is nothing behind it a
        // summary would stand in for. Said out loud only when the fold was asked for by hand.
        if (_history.Count <= KeepVerbatim + 2)
        {
            if (force)
            {
                Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatCompactNothing")) });
            }

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
            text = question,
            files = turn.Attachments.Select(Attachments.Describe),
        });

        _history.Add(turn);
        PushPending();
        _ = RunTurnAsync();
    }

    /// <summary>Throws away the last reply and asks for another to the same question.</summary>
    private void Regenerate()
    {
        if (_turn is not null || _history.Count == 0 || _history[^1].Role != "assistant")
        {
            return;
        }

        _history.RemoveAt(_history.Count - 1);
        Post(new { t = "dropLastAgent" });
        _ = RunTurnAsync();
    }

    /// <summary>
    /// Records a thumbs up or down on a reply, with an optional note, for later training.
    /// </summary>
    /// <remarks>
    /// Written as one JSON object per line under the profile, keyed by the agent, so a run of
    /// feedback is a file that can be read straight into a fine-tune without any further shaping.
    /// It is a nicety: a failure to write it is swallowed rather than allowed to disturb the chat.
    /// </remarks>
    private void SaveFeedback(string verdict, string note, string reply)
    {
        if (verdict is not ("up" or "down"))
        {
            return;
        }

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "feedback");
            Directory.CreateDirectory(folder);

            var record = System.Text.Json.JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now.ToString("o"),
                agent = _agent.Name,
                agentId = _agent.Id,
                model = _agent.Model,
                verdict,
                note,
                reply,
            });

            File.AppendAllText(Path.Combine(folder, _agent.Id + ".jsonl"), record + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a nicety; failing to write it must not disturb the chat or stop the
            // reward below, which is the part the agent actually feels.
        }

        RewardAgent(verdict, note, reply);
    }

    /// <summary>
    /// Turns a rating into guidance the agent will carry into later turns — the reward itself.
    /// </summary>
    /// <remarks>
    /// A thumbs-down with a reason becomes a lesson to avoid; a thumbs-up keeps the answer as the
    /// example to match. Applied to the running agent and to the one saved behind it, so it holds
    /// across sessions. A local model cannot be retrained on the spot, so this feeds the ratings
    /// back through the one channel it does read every turn — its standing instructions.
    /// </remarks>
    private void RewardAgent(string verdict, string note, string reply)
    {
        var saved = ThemeService.Settings.Agents.FirstOrDefault(a => a.Id == _agent.Id);

        foreach (var agent in new[] { _agent, saved })
        {
            if (agent is null)
            {
                continue;
            }

            if (verdict == "up")
            {
                agent.ApprovedExample = reply;
            }
            else if (!string.IsNullOrWhiteSpace(note))
            {
                // Kept unique and bounded: the same complaint twice is one lesson, and only the
                // most recent dozen are ever sent, so the prompt cannot grow without end.
                agent.Lessons.RemoveAll(l => string.Equals(l, note, StringComparison.OrdinalIgnoreCase));
                agent.Lessons.Add(note.Trim());

                while (agent.Lessons.Count > 24)
                {
                    agent.Lessons.RemoveAt(0);
                }
            }
        }

        if (saved is not null)
        {
            ThemeService.Save();
        }
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
            if (turn.Role == "assistant")
            {
                // An assistant turn's attachments are files it handed over, not material to
                // read back: re-reading them into every later request would fill the window
                // with what the agent itself produced.
                conversation.Add(new AgentMessage(AgentRole.Assistant, turn.Text));
                continue;
            }

            var context = ChatContext.Build(turn.Attachments);

            conversation.Add(new AgentMessage(
                AgentRole.User,
                context is null ? turn.Text : turn.Text + "\n\n" + context,
                pictures ? ChatContext.Images(turn.Attachments) : null));
        }

        return conversation;
    }

    /// <summary>Roughly how much of the model's window this conversation now takes up.</summary>
    private int UsedTokens() =>
        ChatContext.EstimateTokens(Conversation(pictures: false).Select(m => m.Text))
        + (_history.Sum(turn => ChatContext.CountImages(turn.Attachments)) * ChatContext.TokensPerImage);

    /// <summary>
    /// Brings a local model up before the first question reaches it. Null when it is ready.
    /// </summary>
    /// <remarks>
    /// Done at the first send rather than when the tab opens: a model is gigabytes of memory and
    /// several seconds of loading, and someone who opened the chat to read it back should not
    /// pay for either. Once it is up the check is a listing call, so later turns are unaffected.
    /// </remarks>
    private async Task<string?> StartLocalModelAsync(CancellationToken cancellationToken)
    {
        if (!_agent.IsLocal)
        {
            return null;
        }

        Phase(AgentPhase.Loading);

        var refused = await LocalAgents.EnsureRunningAsync(_agent, cancellationToken).ConfigureAwait(true);

        if (refused is null)
        {
            Phase(AgentPhase.Thinking);
        }

        return refused;
    }

    private async Task RunTurnAsync()
    {
        var turn = new CancellationTokenSource();
        _turn = turn;

        _reply.Clear();
        Post(new { t = "status", text = "working…", busy = true });
        // The prompt side is known before the request goes out — it is the conversation being
        // sent — so the counter starts with that estimate instead of at zero, and is corrected
        // when the endpoint says what it actually charged.
        _spentIn = UsedTokens();
        _spentOut = 0;
        _counted = false;
        _shared.Clear();
        _thinking.Clear();
        PushSpend(counted: false);

        Phase("thinking");

        try
        {
            // The tunnel first: a local model behind one cannot be asked whether it is loaded
            // until there is a way through to it.
            if (_agent.UsesTunnel)
            {
                Phase(AgentPhase.Tunnelling);

                if (await AgentTunnel.EnsureAsync(_agent, turn.Token).ConfigureAwait(true) is { } blocked)
                {
                    Post(new { t = "note", html = Markdown.Escape(blocked) });

                    return;
                }
            }

            if (await StartLocalModelAsync(turn.Token).ConfigureAwait(true) is { } refused)
            {
                Post(new { t = "note", html = Markdown.Escape(refused) });

                return;
            }

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
            Post(new { t = "status", text = _agent.Origin, busy = false });

            _turn = null;
            turn.Dispose();
        }

        if (_reply.Length > 0)
        {
            _history.Add(new ChatTurn
            {
                Role = "assistant",
                Text = _reply.ToString(),
                Attachments = [.. _shared],
            });
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
                // An empty one only announces that thinking has begun; there is nothing to fold
                // away until words arrive, and showing the toggle early offers an empty box.
                if (item.Text.Length == 0)
                {
                    break;
                }

                // Rendered as markdown like the answer: models write their reasoning in the same
                // shape, lists and code and all.
                _thinking.Append(item.Text);
                Post(new { t = "reasoning", html = Markdown.ToHtml(_thinking.ToString()) });
                break;

            case AgentEventKind.Phase:
                Phase(item.Text);
                break;

            case AgentEventKind.Usage:
                // Not every endpoint reports the prompt side — some proxies leave it out of the
                // stream entirely — so a zero keeps the estimate made when the turn started
                // rather than replacing a rough figure with a wrong one.
                if (item.Input > 0)
                {
                    _spentIn = item.Input;
                }

                _spentOut = item.Output;
                PushSpend(counted: item.Input > 0);
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

            case AgentEventKind.Image:
                // Kept with the turn as well as shown, so reopening the chat still has it.
                _shared.Add(item.Text);
                Post(new { t = "image", label = BotName, src = ImageDataUri(item.Text), path = item.Text });
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
        Post(new { t = "assistant", label = BotName, html = Markdown.ToHtml(_reply.ToString()) });

        // While text is arriving the endpoint has not said what it cost yet, so the count is
        // carried by an estimate and marked as one until the real figure lands.
        if (!_counted)
        {
            _spentOut = ChatContext.EstimateTokens([_reply.ToString()]);
            PushSpend(counted: false);
        }
    }

    /// <summary>
    /// Says what the model is doing at this moment, both in the bar and on the waiting card.
    /// </summary>
    /// <remarks>
    /// A phrase rather than three moving dots: "running a command" and "reading the output" are
    /// the difference between a wait that is understood and one that looks like a hang, and they
    /// are what tells the user whether interrupting would lose anything.
    /// </remarks>
    private void Phase(string phase)
    {
        var what = LocalizationService.T(phase switch
        {
            AgentPhase.Loading => "L_PhaseLoading",
            AgentPhase.Tunnelling => "L_PhaseTunnelling",
            AgentPhase.Deciding => "L_PhaseDeciding",
            AgentPhase.Running => "L_PhaseRunning",
            AgentPhase.RunningElevated => "L_PhaseRunningAdmin",
            AgentPhase.ReadingOutput => "L_PhaseReading",
            AgentPhase.Writing => "L_PhaseWriting",
            AgentPhase.Sharing => "L_PhaseSharing",
            AgentPhase.Drawing => "L_PhaseDrawing",
            AgentPhase.Asking => "L_PhaseAsking",
            AgentPhase.WrappingUp => "L_PhaseWrappingUp",
            _ => "L_PhaseThinking",
        });

        Post(new { t = "status", text = what + "…", busy = true });

        // Thinking needs no caption: the animation already says it, and a word repeating what a
        // moving thing means is noise. The other phases name something the animation cannot.
        Post(new
        {
            t = "thinking",
            on = true,
            label = BotName,
            what = phase == AgentPhase.Thinking ? string.Empty : what,
        });
    }

    /// <summary>What this turn has cost so far.</summary>
    private void PushSpend(bool counted)
    {
        _counted = counted;
        Post(new { t = "spend", input = _spentIn, output = _spentOut, counted });
    }

    // ---- commands ----

    /// <inheritdoc />
    public bool Enabled => _agent.AllowCommands;

    /// <inheritdoc />
    public bool ImagesEnabled => _agent.AllowImages;

    /// <inheritdoc />
    public bool AgentsEnabled => _agent.AllowAgents;

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

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is copied or uploaded: the file is on the user's own machine and stays there. What
    /// the chat gains is a pin they can open or find in Explorer, which is the difference between
    /// a result they can use and a path they have to retype.
    /// </remarks>
    public Task<string> ShareAsync(string path, string note, CancellationToken cancellationToken)
    {
        path = path.Trim().Trim('"');

        if (path.Length == 0)
        {
            return Task.FromResult("No path was given, so nothing was shared.");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            // Reported rather than silently dropped: a model that shares a file it only meant to
            // write will otherwise carry on believing the user has it.
            return Task.FromResult($"There is nothing at {path}, so it was not shared.");
        }

        var full = Path.GetFullPath(path);

        _shared.Add(full);

        Post(new
        {
            t = "shared",
            label = BotName,
            note,
            files = new[] { Attachments.Describe(full) },
        });

        return Task.FromResult($"Shared {full} with the user; it is now in the chat for them to open.");
    }

    /// <summary>
    /// Draws a picture with the local diffusion model and shows it in the chat at full size.
    /// </summary>
    /// <remarks>
    /// The file is kept with the turn as well as shown, so reopening the chat still has it. What
    /// goes back to the model is a plain confirmation, not the picture: it cannot see what it drew,
    /// and inviting it to describe the image only produces a guess the user can already disprove by
    /// looking.
    /// </remarks>
    public async Task<string> GenerateImageAsync(string prompt, string negative, CancellationToken cancellationToken)
    {
        var result = await ImageGen
            .GenerateAsync(prompt, new ImageOptions { Negative = negative }, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Ok || result.PngPath is null)
        {
            return result.Message;
        }

        _shared.Add(result.PngPath);

        Post(new
        {
            t = "image",
            label = BotName,
            src = ImageDataUri(result.PngPath),
            path = result.PngPath,
        });

        return "The picture was generated and is now shown to the user in the chat at full size. "
            + "They can see it; do not describe what is in it.";
    }

    /// <summary>
    /// Puts a request to another configured agent, shows what it produced, and returns it so the
    /// caller can build on it.
    /// </summary>
    /// <remarks>
    /// The agent that is called runs with no tool host of its own, so it answers or draws but
    /// cannot ask a third agent in turn — a call is one level deep by construction, which is what
    /// keeps a mistake from becoming an unbounded chain.
    /// </remarks>
    public async Task<string> AskAgentAsync(string agentName, string request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return "No agent was named.";
        }

        var target = ThemeService.Settings.Agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            var names = string.Join(", ", ThemeService.Settings.Agents.Select(a => a.Name));

            return $"There is no agent named \"{agentName}\". The configured agents are: {names}.";
        }

        if (target.Id == _agent.Id)
        {
            return "An agent cannot ask itself; name a different one.";
        }

        // Cloned and handed no tool host, so it runs on its own and cannot call back into this.
        using var transport = AgentTransports.For(target.Clone());
        var conversation = new List<AgentMessage> { new(AgentRole.User, request) };

        var text = new StringBuilder();
        string? image = null;

        try
        {
            await foreach (var item in transport.SendAsync(conversation, cancellationToken).ConfigureAwait(true))
            {
                switch (item.Kind)
                {
                    case AgentEventKind.Text:
                        text.Append(item.Text);
                        break;

                    case AgentEventKind.Image:
                        image = item.Text;
                        break;

                    case AgentEventKind.Failed:
                        return $"{target.Name} could not answer: {item.Text}";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"{target.Name} could not answer: {ex.Message}";
        }

        if (image is not null)
        {
            _shared.Add(image);
            Post(new { t = "image", label = target.Name, src = ImageDataUri(image), path = image });

            return $"{target.Name} drew a picture; it is shown to the user in the chat.";
        }

        var answer = text.ToString().Trim();

        if (answer.Length == 0)
        {
            return $"{target.Name} returned nothing.";
        }

        // Shown as its own reply under the agent that gave it, and returned so the caller can use it.
        Post(new { t = "assistant", label = target.Name, html = Markdown.ToHtml(answer) });
        Post(new { t = "endTurn" });

        return answer;
    }

    /// <summary>
    /// Opens a web search for the selected text in the user's browser.
    /// </summary>
    /// <remarks>
    /// Handed to the system browser rather than fetched here: the selection is the user's to look
    /// up where they usually do, signed in and with their own extensions, and nothing about it is
    /// sent through the chat or its agent.
    /// </remarks>
    private static void OpenWebSearch(string query)
    {
        var wanted = query.Trim();

        if (wanted.Length == 0)
        {
            return;
        }

        OpenExternally("https://www.google.com/search?q=" + Uri.EscapeDataString(wanted));
    }

    /// <summary>
    /// Puts the selected text to another configured agent, chosen from a small menu, and shows the
    /// exchange in this chat.
    /// </summary>
    /// <remarks>
    /// A side question, so it is shown but not written into this chat's history: the answer comes
    /// from a different agent, and folding it into this one's conversation would leave the model
    /// reading a reply it never gave as if it had. The picker is built here rather than as a dialog
    /// of its own because it is a one-line list of names that wants to appear at the pointer.
    /// </remarks>
    private void AskOtherAgent(string text)
    {
        var selection = text.Trim();

        if (selection.Length == 0 || _turn is not null)
        {
            return;
        }

        var others = ThemeService.Settings.Agents.Where(a => a.Id != _agent.Id).ToList();

        if (others.Count == 0)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatNoOtherAgents")) });
            return;
        }

        var menu = new ContextMenu();

        foreach (var agent in others)
        {
            var item = new MenuItem { Header = agent.PickerName };
            item.Click += (_, _) => _ = RunOtherAgentAsync(agent, selection);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = _webView;
        menu.IsOpen = true;
    }

    /// <summary>Runs one other agent on the selected text and draws the question and its answer.</summary>
    private async Task RunOtherAgentAsync(AiAgent other, string selection)
    {
        if (_turn is not null)
        {
            return;
        }

        var work = new CancellationTokenSource();
        _turn = work;

        var question = string.Format(CultureInfo.CurrentCulture, LocalizationService.T("L_ChatAskAboutThis"), selection);

        Post(new { t = "user", html = Markdown.ToHtml(selection), text = selection });
        Post(new { t = "endTurn" });
        Post(new { t = "thinking", on = true, label = other.Name, what = string.Empty });
        Post(new { t = "status", text = other.Name + "…", busy = true });

        try
        {
            // Cloned and handed no tool host, so it answers on its own and cannot reach back in.
            using var transport = AgentTransports.For(other.Clone());
            var conversation = new List<AgentMessage> { new(AgentRole.User, question) };
            var reply = new StringBuilder();

            await foreach (var item in transport.SendAsync(conversation, work.Token).ConfigureAwait(true))
            {
                switch (item.Kind)
                {
                    case AgentEventKind.Text:
                        reply.Append(item.Text);
                        Post(new { t = "assistant", label = other.Name, html = Markdown.ToHtml(reply.ToString()) });
                        break;

                    case AgentEventKind.Image:
                        Post(new { t = "image", label = other.Name, src = ImageDataUri(item.Text), path = item.Text });
                        break;

                    case AgentEventKind.Failed:
                        Post(new { t = "note", html = Markdown.Escape($"{other.Name}: {item.Text}") });
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user; whatever arrived stays on screen.
        }
        catch (Exception ex)
        {
            Post(new { t = "note", html = Markdown.Escape($"{other.Name}: {ex.Message}") });
        }
        finally
        {
            Post(new { t = "endTurn" });
            Post(new { t = "status", text = _agent.Origin, busy = false });
            _turn = null;
            work.Dispose();
        }
    }

    /// <summary>The picture inlined as a data URI, or empty when it is missing or too large to embed.</summary>
    private static string ImageDataUri(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 12 * 1024 * 1024)
            {
                return string.Empty;
            }

            return $"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
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
