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

    /// <summary>The command now running and the diff it produced, held between its call and result.</summary>
    private string _pendingCommand = string.Empty;
    private string _pendingDiff = string.Empty;

    /// <summary>
    /// This chat's working directory — where its commands run and its files are made. Starts at the
    /// chat's own workspace folder and follows a <c>cd</c> the agent runs, so the location persists
    /// across the fresh shells each command gets.
    /// </summary>
    private string _cwd = string.Empty;

    /// <summary>
    /// The remote machine this chat is working on, when an SSH connection has been attached — then
    /// commands and the file tools run there instead of locally. Null means the local machine.
    /// </summary>
    private RemoteShell? _remote;
    private Guid _remoteSession;

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
        string.IsNullOrWhiteSpace(_chat.BotName) ? _agent.DisplayName : _chat.BotName;

    public AgentChatView(AiAgent agent, ChatSession chat)
    {
        _agent = agent;
        _chat = chat;

        // A chat filed under a project works out of the project folder, so its tools default to the
        // project's files; a loose chat gets its own private workspace.
        _cwd = ProjectContext.WorkingDirectory(chat) ?? Workspace.ForChat(chat);

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

    /// <summary>Raised when a chat asks to open a file (a "go to file" on a diff), so the window can.</summary>
    public static event Action<string>? FileOpenRequested;

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

        EnableFileDrop();
        core.Navigate(PageUrl);
    }

    /// <summary>
    /// Lets files dropped onto the chat be attached, the same as the paperclip.
    /// </summary>
    /// <remarks>
    /// The drop is handled inside the page, not by WPF: a windowed WebView2 swallows the native
    /// drop before WPF's own events fire, which is why dropping never worked before. So the WebView
    /// keeps its own drop (the page's JavaScript reads the files and cancels the default navigation)
    /// and sends the bytes here through a "drop" message, where they are written into the chat's
    /// attachment folder and pinned.
    /// </remarks>
    private void EnableFileDrop() => _webView.AllowExternalDrop = true;

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
                EditTruncate(message);
                Submit(text.GetString() ?? string.Empty, TaskPanel.ParseShared(message));
                break;

            case "askAnswer" when message.TryGetProperty("id", out var askId):
                ResolveAsk(askId.GetString() ?? string.Empty,
                    message.TryGetProperty("answer", out var ans) ? ans.GetString() ?? string.Empty : string.Empty);
                break;

            case "regenerate":
                Regenerate();
                break;

            case "loadEarlier":
                LoadEarlier();
                break;

            case "command" when message.TryGetProperty("name", out var command):
                RunCommand(command.GetString() ?? string.Empty);
                break;

            case "web" when message.TryGetProperty("query", out var query):
                OpenWebSearch(query.GetString() ?? string.Empty);
                break;

            case "snapshot" when message.TryGetProperty("text", out var snap):
                TextSnapshot.CopyToClipboard(snap.GetString() ?? string.Empty);
                break;

            case "openFile" when message.TryGetProperty("path", out var fp):
                FileOpenRequested?.Invoke(fp.GetString() ?? string.Empty);
                break;

            case "revertFile" when message.TryGetProperty("path", out var rp) && message.TryGetProperty("token", out var rt):
                var revertNote = FileTools.Revert(rp.GetString() ?? string.Empty, rt.GetString() ?? string.Empty);
                Post(new { t = "note", html = Markdown.Escape(revertNote) });
                break;

            case "task" when message.TryGetProperty("scope", out var scope) && scope.GetString() == "agent":
                if (TaskPanel.Apply(_agent.Tasks, message))
                {
                    SaveAgentTasks(_agent);
                    PushAgentTasks();
                }

                break;

            case "task":
                if (TaskPanel.Apply(_chat.Tasks, message))
                {
                    Persist();
                    PushTasks();
                }

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

            case "drop":
                Attach(DroppedFiles.Save(message, Workspace.ForChat(_chat)));
                break;

            case "attachFolder":
                AttachFolder();
                break;

            case "attachSsh":
                AttachSession();
                break;

            case "stop":
                StopTurn();
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

    /// <summary>
    /// Stops the running turn, keeping whatever has already arrived. Used both by the composer's
    /// Stop button and by the global panic key, so an agent driving the machine can be halted at once.
    /// </summary>
    public void StopTurn()
    {
        _turn?.Cancel();
        _approval?.TrySetResult('n');
    }

    private void Post(object message)
    {
        if (!_pageReady && message is null)
        {
            return;
        }

        // A tool the model calls (manage_tasks, the file tools) runs on a background thread inside
        // the transport, and the WebView may only be posted to from the UI thread — so a post from
        // off-thread is marshalled rather than throwing.
        if (!_webView.Dispatcher.CheckAccess())
        {
            _webView.Dispatcher.BeginInvoke(() => Post(message));
            return;
        }

        _webView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(message));
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
            "L_ChatAsk", "L_ChatSend", "L_ChatAttachFile", "L_ChatAttachFolder", "L_ChatAttachSsh", "L_ChatDropFiles", "L_ChatRevert", "L_ChatReverted",
            "L_ChatRemove", "L_ChatOpenFile", "L_ChatOpenFolder", "L_ChatReveal",
            "L_ChatRun", "L_ChatSkip", "L_ChatAlways", "L_ChatAlwaysNote", "L_ChatAdminWarn",
            "L_ChatModelTitle", "L_ChatModelOther", "L_ChatModelPlaceholder",
            "L_ChatContextTip", "L_ChatSpentCounted", "L_ChatSpentEstimated", "L_ChatCopied",
            "L_ChatReasoning", "L_ChatStop",
            "L_ChatCopy", "L_ChatRepeat", "L_ChatEdit", "L_ChatLike", "L_ChatDislike",
            "L_ChatRegenerate", "L_ChatDislikeAsk", "L_ChatDislikeSend", "L_ChatThink",
            "L_ChatAskPlaceholder", "L_ChatAskSend",
            "L_ChatDownload", "L_ChatCopyImage", "L_ChatOpenExternal", "L_ChatClose",
            "L_ChatPrev", "L_ChatNext", "L_ChatEarlier", "L_ChatEditing", "L_ChatCancelEdit", "L_ChatCompacting",
            "L_ChatCmdCompact", "L_ChatCmdCompactHint", "L_ChatCmdRetry", "L_ChatCmdRetryHint",
            "L_ChatCmdExport", "L_ChatCmdExportHint",
            "L_ChatCtxCopy", "L_ChatCtxImage", "L_ChatCtxGoToFile", "L_ChatCtxWeb", "L_ChatCtxAsk", "L_ChatCtxAskOther",
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

        // The header: the agent's face, its name, and the model under it.
        Post(new { t = "head", avatar = AvatarDataUri(), title = BotName, subtitle = _agent.ShortModel });
        PushTasks();
        PushAgentTasks();

        // The slash commands this chat answers to. A room reuses the same page but offers none of
        // these, so the set is declared per surface rather than baked into the page.
        Post(new { t = "commands", names = new[] { "compact", "retry", "export" } });

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

        _greeting = Markdown.Escape($"{BotName} — {string.Join(" · ", bits)}");
        RenderHistory();

        Post(new { t = "status", text = _agent.Origin, busy = false });
        PushContext();
    }

    /// <summary>How many turns are drawn at once — a page, so a long chat opens fast.</summary>
    private const int PageSize = 40;

    /// <summary>How many of the most recent turns are currently drawn; grows a page at a time.</summary>
    private int _shown = PageSize;

    /// <summary>The one-line note that heads the chat, kept so a redraw can restore it.</summary>
    private string _greeting = string.Empty;

    /// <summary>
    /// Draws the most recent page of the chat, so reopening a long one is not a wall of work.
    /// </summary>
    /// <remarks>
    /// Only the last <see cref="_shown"/> turns are sent, bracketed as one batch so the page draws
    /// them without following the tail per message. Older turns come in through the "show earlier"
    /// button, which grows the window by a page and redraws.
    /// </remarks>
    private void RenderHistory(bool loadingMore = false)
    {
        // Drawn from the live working history, not the saved copy: the saved one only catches up on
        // the next persist, and "show earlier" can be pressed between turns.
        var turns = _history;
        var start = Math.Max(0, turns.Count - _shown);

        Post(new { t = "clear" });
        Post(new { t = "bulk", on = true });
        Post(new { t = "earlier", count = start });
        Post(new { t = "note", html = _greeting });

        // Everything said before this run is redrawn, so reopening a chat looks like scrolling
        // back through it rather than starting over.
        for (var i = start; i < turns.Count; i++)
        {
            var turn = turns[i];

            if (turn.Role == "command")
            {
                // A saved command is redrawn as the run card it was, output and diff included.
                _activity++;
                var (label, _) = Describe(turn.Command);
                Post(new
                {
                    t = "activity",
                    id = _activity.ToString(),
                    state = "done",
                    label,
                    codeHtml = CodeHighlighter.Highlight(turn.Command),
                });
                Post(new
                {
                    t = "activityDone",
                    id = _activity.ToString(),
                    summary = Summarise(turn.Output),
                    output = turn.Output,
                    diffHtml = DiffHtml(turn.Diff),
                });
                continue;
            }

            if (turn.Role == "assistant")
            {
                if (turn.Text.Length > 0)
                {
                    Post(new { t = "assistant", label = BotName, html = Markdown.ToHtml(turn.Text) });
                }

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
                    idx = i,
                    html = Markdown.ToHtml(turn.Text),
                    text = turn.Text,
                    files = turn.Attachments.Select(Attachments.Describe),
                    tasks = turn.SharedTasks.Select(TaskPanel.Item),
                });
            }

            Post(new { t = "endTurn" });
        }

        Post(new { t = "bulk", on = false, scroll = loadingMore ? "top" : "bottom" });
    }

    /// <summary>Grows the shown window by a page and redraws, for the "show earlier" button.</summary>
    private void LoadEarlier()
    {
        if (_shown >= _history.Count)
        {
            return;
        }

        _shown = Math.Min(_history.Count, _shown + PageSize);
        RenderHistory(loadingMore: true);
    }

    /// <summary>Hands the page this chat's task list and the words its panel is drawn with.</summary>
    private void PushTasks() => Post(new
    {
        t = "tasks",
        list = _chat.Tasks.Select(TaskPanel.Item),
        room = false,
        participants = Array.Empty<string>(),
        statuses = TaskPanel.Statuses(),
        labels = TaskPanel.Labels(),
    });

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

            case "export":
                ExportMarkdown();
                break;
        }
    }

    /// <summary>Writes the conversation out as a Markdown file the user picks — defaulting to the
    /// project's folder for a project chat, so exporting there shares it into the project.</summary>
    private void ExportMarkdown()
    {
        if (_history.Count == 0)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatExportEmpty")) });
            return;
        }

        var name = _chat.BotName.Length > 0 ? _chat.BotName : _agent.Name;
        var title = _chat.Title.Length > 0 ? _chat.Title : name;
        var safe = SafeFileName(title);

        var project = _chat.ProjectId.Length > 0
            ? ProjectStore.Projects.FirstOrDefault(p => p.Id == _chat.ProjectId)
            : null;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.T("L_ChatExportTitle"),
            Filter = "Markdown (*.md)|*.md",
            FileName = safe + ".md",
            DefaultExt = ".md",
        };

        if (project is { Folder.Length: > 0 } && Directory.Exists(project.Folder))
        {
            dialog.InitialDirectory = project.Folder;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BuildMarkdown(name, title));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(new { t = "note", html = Markdown.Escape(string.Format(LocalizationService.T("L_ChatExportFailed"), ex.Message)) });
            return;
        }

        var inProject = project is { Folder.Length: > 0 }
            && dialog.FileName.StartsWith(project.Folder, StringComparison.OrdinalIgnoreCase);

        Post(new
        {
            t = "note",
            html = Markdown.Escape(string.Format(
                LocalizationService.T(inProject ? "L_ChatExportedProject" : "L_ChatExported"),
                Path.GetFileName(dialog.FileName))),
        });

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not open export: {ex.Message}");
        }
    }

    private string BuildMarkdown(string agentName, string title)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title);
        sb.AppendLine().Append('*').Append(agentName).Append(" · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).AppendLine("*").AppendLine();

        foreach (var turn in _history)
        {
            switch (turn.Role)
            {
                case "user":
                    sb.AppendLine("## " + LocalizationService.T("L_ChatExportYou")).AppendLine().AppendLine(turn.Text.Trim()).AppendLine();
                    break;
                case "assistant":
                    sb.AppendLine("## " + agentName).AppendLine().AppendLine(turn.Text.Trim()).AppendLine();
                    break;
                case "command":
                    sb.AppendLine("```").Append("$ ").AppendLine(turn.Command.Trim());
                    if (turn.Output.Trim().Length > 0)
                    {
                        sb.AppendLine(turn.Output.Trim());
                    }

                    sb.AppendLine("```").AppendLine();
                    break;
            }
        }

        return sb.ToString();
    }

    private static string SafeFileName(string name)
    {
        var cleaned = new string(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        cleaned = cleaned.Length > 60 ? cleaned[..60].TrimEnd() : cleaned;
        return cleaned.Length > 0 ? cleaned : "chat";
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

            // If the history was too large to summarise in one pass, the fold above did nothing;
            // dropping the oldest turns still frees the room the user asked for.
            TrimToContext();
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
            if (_history[i].Role == "command")
            {
                var ran = _history[i];
                transcript.Append("Assistant ran: ").AppendLine(ran.Command);
                continue;
            }

            transcript.Append(_history[i].Role == "user" ? "User: " : "Assistant: ")
                .AppendLine(_history[i].Text);
        }

        // A summary is itself a request to the model, and if the part being summarised is already
        // bigger than the model can hold, that request is refused just as the real turn would be —
        // most often on a small local model. Rather than make a call that cannot succeed, the fold
        // is skipped here, and the oldest turns are dropped instead by TrimToContext.
        if (ChatContext.EstimateTokens([transcript.ToString()]) > _agent.ContextWindow * 0.9)
        {
            return;
        }

        var summary = new StringBuilder();
        Post(new { t = "compact", state = "start" });

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

            var drawn = 0;

            await foreach (var item in plain.SendAsync(ask, cancellationToken).ConfigureAwait(true))
            {
                if (item.Kind == AgentEventKind.Text)
                {
                    summary.Append(item.Text);

                    // The summary streams; the card is nudged every so often rather than on every
                    // fragment, which would be dozens of messages a second for no more information.
                    if (summary.Length - drawn >= 400)
                    {
                        drawn = summary.Length;
                        Post(new { t = "compact", state = "progress", chars = summary.Length });
                    }
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
        finally
        {
            // Whatever happened — done, failed, or stopped — the progress card is taken down.
            Post(new { t = "compact", state = "done" });
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

        // Redraw so the collapse is seen: the wall of old turns becomes the one summary line.
        // Without this the history shrank underneath but the screen kept the old messages.
        _shown = PageSize;
        RenderHistory();

        Post(new
        {
            t = "note",
            html = Markdown.Escape("The earlier part of this chat was summarised to make room."),
        });

        Persist();
    }

    /// <summary>
    /// Drops the oldest turns until the conversation fits the context window — a last resort for
    /// when a summary could not be made, most often a local model too small to summarise its own
    /// history in one pass.
    /// </summary>
    /// <remarks>
    /// The persona and standing instructions travel as the system message, not in the history, so
    /// they are never the part dropped — only the earliest of the back-and-forth. Their size is
    /// still taken off the budget, because they are sent ahead of it on every request. This is what
    /// keeps a small local model from refusing a whole long chat rather than answering the recent
    /// part of it.
    /// </remarks>
    private void TrimToContext()
    {
        if (_agent.Provider == AiProvider.ImageGen)
        {
            return;
        }

        var reserved = ChatContext.EstimateTokens([_agent.Instructions]);
        var budget = (_agent.ContextWindow * 0.85) - reserved;
        var dropped = 0;

        while (_history.Count > 2 && UsedTokens() > budget)
        {
            _history.RemoveAt(0);
            dropped++;
        }

        // Both wire formats expect the first turn to be the user's; a tail left starting on a reply
        // is dropped down to the next question.
        while (_history.Count > 0 && _history[0].Role != "user")
        {
            _history.RemoveAt(0);
            dropped++;
        }

        if (dropped > 0)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatTrimmed")) });
            Persist();
        }
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

        MaybeAutoTitle();
    }

    private bool _autoTitleTried;

    /// <summary>
    /// Once the first exchange exists, replaces the title we derived from the first message with a
    /// short model-written one — a real summary of what the chat is about — unless the user has
    /// already named it themselves. Runs once, in the background, and quietly keeps the derived
    /// title if the model or network is unavailable.
    /// </summary>
    private async void MaybeAutoTitle()
    {
        if (_autoTitleTried)
        {
            return;
        }

        // An image agent's transport draws rather than writes, so a title request would return a
        // picture — leave those on the derived title.
        if (_agent.Provider == AiProvider.ImageGen)
        {
            return;
        }

        var user = _history.FirstOrDefault(t => t.Role == "user");
        var assistant = _history.FirstOrDefault(t => t.Role == "assistant");

        if (user is null || assistant is null || user.Text.Trim().Length == 0)
        {
            return;
        }

        _autoTitleTried = true;

        // Only replace a title we derived ourselves — never one the user set by hand.
        var derived = ChatSession.TitleFrom(user.Text);
        if (_chat.Title.Length > 0 && _chat.Title != derived)
        {
            return;
        }

        var title = await GenerateTitleAsync(user.Text, assistant.Text).ConfigureAwait(true);

        // Skip if it produced nothing, or the user renamed the chat while it was thinking.
        if (title.Length == 0 || _disposed || (_chat.Title.Length > 0 && _chat.Title != derived))
        {
            return;
        }

        _chat.Title = title;
        _chat.Touch();
        ChatStore.Save(_chat);
    }

    private async Task<string> GenerateTitleAsync(string question, string answer)
    {
        try
        {
            // A bare clone: no system prompt, no roleplay card, no preamble or lessons — so the reply
            // is a title and nothing else. Tools are off; this only wants a few words back.
            var titler = _agent.Clone();
            titler.SystemPrompt = string.Empty;
            titler.EnvironmentPreamble = string.Empty;
            titler.IsRoleplay = false;
            titler.Lessons.Clear();
            titler.MaxTokens = 40;
            titler.Thinking = false;

            using var transport = AgentTransports.For(titler, tools: null);

            var prompt =
                "Write a very short title (3–6 words) for this chat: no quotes, no trailing period, "
                + "in the same language as the conversation. Reply with only the title.\n\n"
                + "User: " + Shorten(question, 800) + "\n\nAssistant: " + Shorten(answer, 800);

            var text = new StringBuilder();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

            await foreach (var item in transport.SendAsync([new AgentMessage(AgentRole.User, prompt)], timeout.Token).ConfigureAwait(true))
            {
                if (item.Kind == AgentEventKind.Text)
                {
                    text.Append(item.Text);
                }
                else if (item.Kind == AgentEventKind.Failed)
                {
                    return string.Empty;
                }
            }

            return CleanTitle(text.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Auto-title failed: {ex.Message}");
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Shorten(string text, int max)
    {
        text = text.Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }

    private static string CleanTitle(string raw)
    {
        var title = raw.Trim().ReplaceLineEndings(" ").Trim();

        // Models sometimes wrap the title in quotes or lead with "Title:"; strip that.
        if (title.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            title = title["Title:".Length..].Trim();
        }

        title = title.Trim('"', '\'', '«', '»', '“', '”', ' ', '.', ':', '—', '-');

        while (title.Contains("  "))
        {
            title = title.Replace("  ", " ");
        }

        return title.Length > 70 ? title[..70].TrimEnd() + "…" : title;
    }

    // ---- a turn ----

    /// <summary>
    /// When a message carries an edit position, drops that turn and everything after it before the
    /// edited text is sent — so an edit replaces the message and its consequences rather than piling
    /// a near-duplicate onto the end. The view is redrawn to match before the new turn is added.
    /// </summary>
    private void EditTruncate(JsonElement message)
    {
        if (_turn is not null
            || !message.TryGetProperty("editIndex", out var slot)
            || !slot.TryGetInt32(out var at)
            || at < 0
            || at >= _history.Count)
        {
            return;
        }

        _history.RemoveRange(at, _history.Count - at);
        _shown = PageSize;
        RenderHistory();
        Persist();
    }

    private void Submit(string text, List<TaskItem>? sharedTasks = null)
    {
        var question = text.Trim();
        var shared = sharedTasks ?? [];

        // A message may carry only shared tasks and no words of its own, so an empty question is
        // still sent when there are tasks pinned to it.
        if (_turn is not null || (question.Length == 0 && shared.Count == 0))
        {
            return;
        }

        // The attachments travel with the message they were pinned for, and the composer is
        // cleared: a pin that stayed put would silently re-send the file on every later turn.
        var turn = new ChatTurn { Role = "user", Text = question, Attachments = [.. _pending], SharedTasks = shared };
        _pending.Clear();

        Post(new
        {
            t = "user",
            idx = _history.Count,
            html = Markdown.ToHtml(question),
            text = question,
            files = turn.Attachments.Select(Attachments.Describe),
            tasks = shared.Select(TaskPanel.Item),
        });

        _history.Add(turn);
        UpdateRemote(turn.Attachments);
        PushPending();
        _ = RunTurnAsync();
    }

    /// <summary>
    /// Points this chat's tools at a remote machine when an SSH connection is attached, so commands
    /// and file edits land there. Sticky: it holds until a different connection is attached.
    /// </summary>
    private void UpdateRemote(IEnumerable<string> attachments)
    {
        var session = attachments
            .Where(Attachments.IsSshSession)
            .Select(SessionCatalog.Find)
            .LastOrDefault(s => s is not null);

        if (session is null || session.Id == _remoteSession)
        {
            return;
        }

        _remote?.Dispose();
        _remote = new RemoteShell(session);
        _remoteSession = session.Id;

        Post(new
        {
            t = "note",
            html = Markdown.Escape(
                $"Commands and file tools now run on {session.Name} ({session.Username}@{session.Host})."),
        });
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
            // Command turns are kept in the history for the transcript, but the model already saw
            // the tool result within the turn it ran in; re-sending them would be a role the APIs
            // do not know, so they are left out of what goes back up.
            if (turn.Role == "command")
            {
                continue;
            }

            if (turn.Role == "assistant")
            {
                // Both APIs want the roles to alternate; a turn's narration split across several
                // assistant segments by the commands between them is rejoined into one so two
                // assistant messages never sit side by side.
                if (turn.Text.Length == 0)
                {
                    continue;
                }

                if (conversation.Count > 0 && conversation[^1].Role == AgentRole.Assistant)
                {
                    var merged = conversation[^1];
                    conversation[^1] = merged with { Text = merged.Text + "\n\n" + turn.Text };
                    continue;
                }

                conversation.Add(new AgentMessage(AgentRole.Assistant, turn.Text));
                continue;
            }

            var context = ChatContext.Build(turn.Attachments);

            // The shared-task card is drawn from structured data in the chat, but the model reads
            // it as text folded into the message it was sent with.
            var body = turn.Text;
            var shared = TaskPanel.SharedText(turn.SharedTasks);

            if (shared.Length > 0)
            {
                body = body.Length > 0 ? body + "\n\n" + shared : shared;
            }

            conversation.Add(new AgentMessage(
                AgentRole.User,
                context is null ? body : body + "\n\n" + context,
                pictures ? ChatContext.Images(turn.Attachments) : null));
        }

        // The current task lists ride along on the last thing the user said, rebuilt fresh each
        // send like the attachments — so the agent begins a turn already knowing the tasks and
        // their ids, and never has to spend a "list" call to find them.
        var seed = TaskPanel.SeedBlock(_chat.Tasks, _agent.Tasks);

        if (seed.Length > 0)
        {
            var last = conversation.FindLastIndex(m => m.Role == AgentRole.User);

            if (last >= 0)
            {
                conversation[last] = conversation[last] with
                {
                    Text = conversation[last].Text + "\n\n" + seed,
                };
            }
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

        // The history's length before the turn adds anything, so a turn that produced nothing can
        // be told from one that only ran commands — the user message is dropped only in the former.
        var startCount = _history.Count;

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

            // The guaranteed fit: whatever the summary managed, the oldest turns are dropped until
            // the request is within the window, so a small local model answers the recent part
            // rather than refusing the whole chat.
            TrimToContext();

            // The environment preamble is rebuilt here so it carries the working directory as it
            // stands now — a `cd` from the last turn is reflected before this one is sent. Over an
            // attached connection it describes the remote machine the tools now act on.
            var preamble = _remote is not null
                ? SystemInfo.RemotePreamble(_remote.Host, _remote.User, _remote.Cwd)
                : SystemInfo.Preamble(_cwd);

            // A project chat carries its project's orientation in the preamble, so the agent knows
            // the description, notes, sources and layout without the user re-explaining each time.
            _agent.EnvironmentPreamble = ProjectContext.Build(_chat) is { } project
                ? preamble + "\n\n" + project
                : preamble;

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

            // The turn's token cost travels with its close, so the finished reply can carry a small
            // spend badge in its action row — how much this answer actually cost, kept beside it.
            Post(new { t = "endTurn", input = _spentIn, output = _spentOut });
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
        else if (_shared.Count > 0)
        {
            // No closing words, but files or pictures were produced — keep them on a turn of their own.
            _history.Add(new ChatTurn { Role = "assistant", Text = string.Empty, Attachments = [.. _shared] });
        }
        else if (_history.Count == startCount)
        {
            // The turn produced nothing at all — no text, no commands — so the question is dropped
            // rather than re-sent as unanswered history. Commands alone are enough to keep it.
            _history.RemoveAt(startCount - 1);
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
                // The reply so far is committed before the command, so the two do not interleave —
                // and the text segment is kept in the history so reopening the chat still shows the
                // narration that led up to the command.
                _paint.Stop();
                PaintReply();

                if (_reply.Length > 0)
                {
                    _history.Add(new ChatTurn { Role = "assistant", Text = _reply.ToString() });
                    _reply.Clear();
                }

                Post(new { t = "endTurn" });

                _activity++;
                var (label, _) = Describe(item.Text);

                // The command itself rides on its own plate, syntax-coloured, rather than trailing
                // the label inline — a multiline script there overran the "running" word.
                Post(new
                {
                    t = "activity",
                    id = _activity.ToString(),
                    state = "running",
                    label,
                    codeHtml = CodeHighlighter.Highlight(item.Text),
                });
                break;

            case AgentEventKind.ToolResult:
                Post(new
                {
                    t = "activityDone",
                    id = _activity.ToString(),
                    summary = Summarise(item.Text),
                    output = item.Text,
                    diffHtml = DiffHtml(_pendingDiff),
                });

                // The command, its output and what it changed are kept as a turn of their own so
                // they survive reopening the chat.
                _history.Add(new ChatTurn
                {
                    Role = "command",
                    Command = _pendingCommand,
                    Output = item.Text,
                    Diff = _pendingDiff,
                });

                _pendingCommand = string.Empty;
                _pendingDiff = string.Empty;
                break;

            case AgentEventKind.ToolRefused:
                Post(new { t = "activityDone", id = _activity.ToString(), summary = "skipped", output = "" });
                _history.Add(new ChatTurn { Role = "command", Command = _pendingCommand, Output = "(declined)" });
                _pendingCommand = string.Empty;
                _pendingDiff = string.Empty;
                break;

            case AgentEventKind.Image:
                // Kept with the turn as well as shown, so reopening the chat still has it.
                _shared.Add(item.Text);
                AgentFiles.Touched(item.Text);
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
        var what = LocalizationService.T(AgentPhase.Key(phase));

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
    /// <remarks>Always on: keeping the task list is not a privilege, it is the point of the header.</remarks>
    public bool TasksEnabled => true;

    /// <inheritdoc />
    public Task<string> ManageTasksAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var text = TaskPanel.HandleTool(
            argumentsJson, _chat.Tasks, _agent.Tasks,
            out var changedShared, out var changedMine, out var report);

        if (changedShared)
        {
            Persist();
            PushTasks();
        }

        if (changedMine)
        {
            SaveAgentTasks(_agent);
            PushAgentTasks();
        }

        if (report.Length > 0)
        {
            Post(new { t = "note", html = Markdown.Escape($"{BotName}: {report}") });
        }

        return Task.FromResult(text);
    }

    /// <summary>
    /// Writes an agent's own task list through to the saved copy behind it, so a plan the model
    /// made survives the run.
    /// </summary>
    private static void SaveAgentTasks(AiAgent agent)
    {
        var saved = ThemeService.Settings.Agents.FirstOrDefault(a => a.Id == agent.Id);

        if (saved is not null && !ReferenceEquals(saved, agent))
        {
            saved.Tasks = [.. agent.Tasks];
        }

        ThemeService.Save();
    }

    /// <summary>Hands the page this chat's one agent and its private notebook.</summary>
    private void PushAgentTasks() => Post(new
    {
        t = "agentTasks",
        agents = new[] { new { name = BotName, list = _agent.Tasks.Select(TaskPanel.Item) } },
        statuses = TaskPanel.Statuses(),
        labels = TaskPanel.AgentLabels(),
    });

    /// <summary>True once the user has allowed this chat's agent to drive the mouse, so only the first click asks.</summary>
    private bool _mouseAllowed;

    /// <inheritdoc />
    /// <remarks>Runs the window call on the UI thread — focus is reliable from there — and shows the user what it did.</remarks>
    public async Task<AgentToolResult> ManageWindowAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        // Closing a window can lose unsaved work, so it is always put to the user first.
        if (WindowTools.ActionOf(argumentsJson) is "close" or "quit"
            && !await ApproveAsync(LocalizationService.T("L_ConfirmCloseWindow"), elevated: false, cancellationToken).ConfigureAwait(true))
        {
            return new AgentToolResult("The user declined closing the window.");
        }

        var outcome = Dispatcher.Invoke(() => WindowTools.Handle(argumentsJson));
        return await WindowOutcomeAsync(outcome, BotName, _agent.Vision).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task<string> ControlMouseAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        // The first time the agent actually clicks or drags, the user is asked to allow it; after
        // that it drives freely until the chat is closed. Moving and reading the position do not ask.
        var action = InputTools.ActionOf(argumentsJson);
        var clicks = action is "click" or "double" or "doubleclick" or "double_click"
            or "right" or "rightclick" or "right_click" or "middle" or "drag";

        if (clicks && !_mouseAllowed)
        {
            if (!await ApproveAsync(LocalizationService.T("L_ConfirmMouse"), elevated: false, cancellationToken).ConfigureAwait(true))
            {
                return "The user declined mouse control.";
            }

            _mouseAllowed = true;
        }

        var result = Dispatcher.Invoke(() => InputTools.Handle(argumentsJson));
        Post(new { t = "note", html = Markdown.Escape($"{BotName}: {result}") });
        return result;
    }

    /// <inheritdoc />
    public Task<string> TypeKeysAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var result = Dispatcher.Invoke(() => InputTools.HandleKey(argumentsJson));
        Post(new { t = "note", html = Markdown.Escape($"{BotName}: {result}") });
        return Task.FromResult(result);
    }

    /// <summary>
    /// Turns a window call's outcome into a tool result, showing a screenshot in the chat. A model
    /// that can see gets the picture; a text-only one gets the text read off it by on-device OCR,
    /// so it can still work from what is on screen.
    /// </summary>
    private async Task<AgentToolResult> WindowOutcomeAsync(WindowTools.Outcome outcome, string speaker, bool vision)
    {
        Post(new { t = "note", html = Markdown.Escape($"{speaker}: {outcome.Text}") });

        if (outcome.Png is not { Length: > 0 } png)
        {
            return new AgentToolResult(outcome.Text);
        }

        // The user always sees the shot, whatever the model can take.
        var uri = "data:image/png;base64," + Convert.ToBase64String(png);
        Post(new { t = "image", label = speaker, src = uri, path = string.Empty });

        if (vision)
        {
            return new AgentToolResult(outcome.Text, new AgentImage("image/png", Convert.ToBase64String(png)));
        }

        var text = await OcrService.ReadAsync(png).ConfigureAwait(true);
        var body = text.Length > 0
            ? $"{outcome.Text}\n\nText recognised on screen (OCR):\n{text}"
            : $"{outcome.Text}\n\n(No text could be recognised on screen.)";

        return new AgentToolResult(body);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads and listings run straight through; a write or edit is put to the user the same way a
    /// command is, then shown as a change card with its diff and a jump to the file.
    /// </remarks>
    public async Task<string> FileToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        // Over an attached connection, every file tool acts on the remote machine.
        if (_remote is not null)
        {
            return await RemoteFileToolAsync(name, argumentsJson, cancellationToken).ConfigureAwait(true);
        }

        if (name == AgentTransports.Files.Read)
        {
            return FileTools.Read(argumentsJson, _cwd);
        }

        if (name == AgentTransports.Files.List)
        {
            return FileTools.List(argumentsJson, _cwd);
        }

        var path = FileTools.PathOf(argumentsJson, _cwd);

        if (path is null)
        {
            return "No path was given.";
        }

        if (!await ApproveAsync($"{name} {path}", elevated: false, cancellationToken).ConfigureAwait(true))
        {
            return "The user declined this change.";
        }

        var result = name == AgentTransports.Files.Write
            ? FileTools.Write(argumentsJson, _cwd)
            : FileTools.Edit(argumentsJson, _cwd);

        if (result.Ok)
        {
            AgentFiles.Touched(path);
            ShowFileChange(name == AgentTransports.Files.Write ? "wrote" : "edited", path, result.Diff, result.Undo);
        }
        else
        {
            // A failed change — most often edit_file not finding its exact old text — used to be
            // silent to the user, which reads as "the agent said it changed the file but did not".
            // Show it, so a miss is visible and not mistaken for the tool being broken.
            Post(new { t = "note", html = Markdown.Escape($"⚠ {name}: {result.Message}") });
        }

        return result.Message;
    }

    /// <summary>The file tools when the chat is working over SSH: read and list run through, a write or edit is approved and shown.</summary>
    private async Task<string> RemoteFileToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        if (name == AgentTransports.Files.Read)
        {
            return await _remote!.ReadFileAsync(argumentsJson, cancellationToken).ConfigureAwait(true);
        }

        if (name == AgentTransports.Files.List)
        {
            return await _remote!.ListAsync(argumentsJson, cancellationToken).ConfigureAwait(true);
        }

        var path = _remote!.PathOf(argumentsJson);

        if (!await ApproveAsync($"{name} {path} (on {_remote.Host})", elevated: false, cancellationToken).ConfigureAwait(true))
        {
            return "The user declined this change.";
        }

        var (message, diff) = name == AgentTransports.Files.Write
            ? await _remote.WriteFileAsync(argumentsJson, cancellationToken).ConfigureAwait(true)
            : await _remote.EditFileAsync(argumentsJson, cancellationToken).ConfigureAwait(true);

        ShowFileChange(name == AgentTransports.Files.Write ? "wrote" : "edited", $"{path} (on {_remote.Host})", diff);
        return message;
    }

    /// <summary>
    /// Draws a file the agent wrote or edited as a change card — the path, jumpable, and the diff
    /// when the file is under git — and keeps it in the history so it survives reopening.
    /// </summary>
    private void ShowFileChange(string verb, string path, string diff, string undo = "")
    {
        _activity++;
        var pathHtml = $"<span data-file=\"{Markdown.Escape(path)}\">{Markdown.Escape(path)}</span>";

        Post(new { t = "activity", id = _activity.ToString(), state = "done", label = verb, codeHtml = pathHtml });
        Post(new
        {
            t = "activityDone",
            id = _activity.ToString(),
            diffHtml = DiffHtml(diff),
            revert = undo,
            revertPath = undo.Length > 0 ? path : string.Empty,
        });

        _history.Add(new ChatTurn { Role = "command", Command = $"{verb} {path}", Output = string.Empty, Diff = diff });
        Persist();
    }

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
        _pendingCommand = command;
        _pendingDiff = string.Empty;

        // With a connection attached, the command runs on the remote over the live session rather
        // than locally. Elevation is a local notion, so an elevated command stays on this machine.
        if (_remote is not null && !elevated)
        {
            return await _remote.RunAsync(command, cancellationToken).ConfigureAwait(true);
        }

        // Not attached and the model reached for ssh/plink locally: those run without a TTY here, so
        // a password login hangs and the password would sit on the command line. Route the intent
        // through RedBloom's own SSH client instead, connecting with the saved session's credentials.
        if (!elevated && AgentSsh.Looks(command))
        {
            return await RouteSshAsync(command, cancellationToken).ConfigureAwait(true);
        }

        // The repository state before the command, so its edits can be told apart from what was
        // already uncommitted. Cheap when there is no repo — git simply reports nothing.
        var snapshot = GitDiff.Before(command);

        string output;

        if (!elevated)
        {
            // Runs in this chat's own working directory, and the directory the command leaves the
            // shell in is remembered — so a `cd` carries to the next command.
            var result = await CommandRunner.RunInAsync(command, _cwd, cancellationToken).ConfigureAwait(true);
            _cwd = result.Cwd;
            output = result.Output;
        }
        else if (!ElevatedHost.IsRunning
                 && await ElevatedHost.StartAsync(cancellationToken).ConfigureAwait(true) is { } refused)
        {
            output = $"This command needed administrator rights and did not get them: {refused}";
        }
        else
        {
            output = await ElevatedHost.RunAsync(command, cancellationToken).ConfigureAwait(true);
        }

        _pendingDiff = GitDiff.After(snapshot);
        return output;
    }

    /// <summary>
    /// Runs what an <c>ssh</c>/<c>plink</c> command intended over RedBloom's built-in SSH client
    /// (SSH.NET) instead of spawning an external one: it matches the target to a saved session,
    /// attaches it so this and later commands run there, and executes any remote command. When no
    /// saved session matches, it asks for the session to be added rather than falling back to an
    /// external ssh — so a password never lands on the command line or reaches the model.
    /// </summary>
    private async Task<string> RouteSshAsync(string command, CancellationToken cancellationToken)
    {
        if (AgentSsh.Parse(command) is not { } target)
        {
            return "RedBloom runs SSH through its own client, not an external ssh. Attach the session for that host (or add it in the sidebar) and I'll run commands over it.";
        }

        if (target.FileTransfer)
        {
            return "External scp/sftp is disabled. Attach the session for that host, then use the file tools (read_file / write_file) to move files over the built-in SSH.";
        }

        if (AgentSsh.Match(target) is not { } session)
        {
            var who = (target.User.Length > 0 ? target.User + "@" : string.Empty) + target.Host + (target.Port != 22 ? ":" + target.Port : string.Empty);
            return $"External ssh/plink is disabled — I use RedBloom's built-in SSH (SSH.NET). No saved session matches {who}. Add it in the sidebar (with its password or key) or attach it, and I'll connect over the built-in client; the password then never goes on the command line or to me.";
        }

        if (_remoteSession != session.Id)
        {
            _remote?.Dispose();
            _remote = new RemoteShell(session);
            _remoteSession = session.Id;
        }

        Post(new
        {
            t = "note",
            html = Markdown.Escape($"Using the built-in SSH for {session.Username}@{session.Host} — external ssh/plink is not used."),
        });

        if (target.RemoteCommand.Length == 0)
        {
            return $"Connected to {session.Username}@{session.Host} over the built-in SSH. run_command and the file tools now act on this host — run commands directly, without an ssh prefix.";
        }

        return await _remote!.RunAsync(target.RemoteCommand, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is copied or uploaded: the file is on the user's own machine and stays there. What
    /// the chat gains is a pin they can open or find in Explorer, which is the difference between
    /// a result they can use and a path they have to retype.
    /// </remarks>
    // ---- ask the user ----

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingAsks = new();

    /// <summary>
    /// Puts the model's question to the user in the chat and waits for their answer. Preset options
    /// are shown as buttons; the user may always type their own answer instead.
    /// </summary>
    public Task<string> AskUserAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var question = string.Empty;
        var options = new List<string>();

        try
        {
            var root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
            if (root.TryGetProperty(AgentTransports.AskUser.Question, out var q) && q.ValueKind == JsonValueKind.String)
            {
                question = q.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty(AgentTransports.AskUser.Options, out var o) && o.ValueKind == JsonValueKind.Array)
            {
                options = o.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => (x.GetString() ?? string.Empty).Trim())
                    .Where(s => s.Length > 0)
                    .Take(6)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Fall through with whatever parsed.
        }

        question = question.Trim();
        if (question.Length == 0)
        {
            return Task.FromResult("(no question was provided to ask)");
        }

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAsks[id] = tcs;

        // If the turn is cancelled while waiting, let the model move on instead of hanging forever.
        var registration = cancellationToken.Register(() =>
        {
            if (_pendingAsks.TryRemove(id, out var pending))
            {
                pending.TrySetResult("(the user did not answer — the request was cancelled)");
                Post(new { t = "askDone", id });
            }
        });

        Post(new { t = "ask", id, question, options });

        return Finish();

        async Task<string> Finish()
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
                _pendingAsks.TryRemove(id, out _);
            }
        }
    }

    /// <summary>The user answered a question the model asked; hand the text back to the waiting call.</summary>
    private void ResolveAsk(string id, string answer)
    {
        if (id.Length > 0 && _pendingAsks.TryRemove(id, out var tcs))
        {
            answer = answer.Trim();
            tcs.TrySetResult(answer.Length > 0 ? answer : "(the user gave an empty answer)");
        }
    }

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
        AgentFiles.Touched(full);

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
        AgentFiles.Touched(result.PngPath);

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
            AgentFiles.Touched(image);
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

    /// <summary>
    /// Renders a unified diff as page-safe HTML: each file a block carrying its full path for the
    /// "go to file" action, each line coloured by its + / - / context. Empty when nothing changed.
    /// </summary>
    private static string DiffHtml(string diff) => GitDiff.RenderHtml(diff);

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
        _remote?.Dispose();
        _transport.Dispose();
        SessionEnded?.Invoke(this, "The agent session was closed.");
        _webView.Dispose();
    }
}
