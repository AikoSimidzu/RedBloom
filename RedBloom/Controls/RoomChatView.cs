using System.IO;
using System.Text;
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
/// A group conversation: several agents in one chat, taking the floor by the room's rule.
/// </summary>
/// <remarks>
/// It hosts the same <c>chat.html</c> page a one-agent chat does, so replies, code blocks and
/// pictures render identically; what differs is on this side. Each participant is asked in turn,
/// its reply is drawn under its own name and colour, and the rule for who speaks next — everyone,
/// in rotation, by @-mention, or chosen by a moderator — is applied here. An agent that names
/// another with an @ hands it the floor, which is how one agent reaches another, an image agent
/// included.
/// </remarks>
public sealed class RoomChatView : UserControl, IDisposable, IAgentToolHost
{
    private const string VirtualHost = "redbloom.assets";
    private const string PageUrl = $"https://{VirtualHost}/chat.html";

    /// <summary>An upper bound on agent replies per user message, so a mention loop cannot run away.</summary>
    private const int MaxAgentTurnsPerMessage = 12;

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedBloom", "WebView2");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: folder);
    });

    private readonly ChatRoom _room;
    private readonly WebView2 _webView = new();

    /// <summary>Attached in the composer and not yet sent, exactly as a one-to-one chat keeps them.</summary>
    private readonly List<string> _pending = [];

    private CancellationTokenSource? _turn;
    private bool _pageReady;
    private bool _disposed;

    /// <summary>The agent whose turn is running, so a command approval is attributed to it.</summary>
    private AiAgent? _speaking;

    private TaskCompletionSource<char>? _approval;
    private string _suggested = string.Empty;
    private int _activity;

    public RoomChatView(ChatRoom room)
    {
        _room = room;
        ApplyWebViewBackground();
        Content = _webView;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Matches the terminal's background rule so the transparent page does not fall back to the
    /// white WebView2 default — the reason a room opened to a blank white pane.
    /// </summary>
    private void ApplyWebViewBackground()
    {
        var s = ThemeService.Settings;
        var background = ThemeService.ParseColor(s.TerminalBackground, System.Windows.Media.Colors.Black);

        _webView.DefaultBackgroundColor = s.BackgroundMode == BackgroundMode.None
            ? System.Drawing.Color.FromArgb(255, background.R, background.G, background.B)
            : System.Drawing.Color.FromArgb(0, background.R, background.G, background.B);
    }

    /// <summary>The agents currently in the room, resolved from the saved ids, in listed order.</summary>
    private List<AiAgent> Participants() =>
        [.. _room.ParticipantIds
            .Select(id => ThemeService.Settings.Agents.FirstOrDefault(a => a.Id == id))
            .Where(a => a is not null)
            .Select(a => a!)];

    /// <summary>
    /// Re-sends the roster to the page after the room has been edited, so the composer's @-mention
    /// picker follows the change at once instead of only after the room is reopened.
    /// </summary>
    /// <remarks>
    /// The turn-taking already sees the change — it reads the participants off the same room object
    /// the edit dialog wrote to — so only the picker, filled once when the page loaded, is behind.
    /// A pin on an agent that has just been removed is released by the page when this arrives.
    /// </remarks>
    public void RefreshParticipants()
    {
        var names = Participants().Select(a => a.Name).ToList();

        Post(new { t = "mentions", names });
        Post(new { t = "note", html = Markdown.Escape($"{_room.Title} · {string.Join(", ", names)}") });
    }

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
        core.Settings.IsSwipeNavigationEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, args) => { args.Handled = true; OpenExternally(args.Uri); };
        EnableFileDrop();
        core.Navigate(PageUrl);
    }

    /// <summary>Lets files dropped onto the room be attached, the same as the paperclip.</summary>
    private void EnableFileDrop()
    {
        _webView.AllowExternalDrop = false;
        _webView.AllowDrop = true;

        _webView.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };

        _webView.Drop += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                Attach(paths);
            }

            e.Handled = true;
        };
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
                break;

            case "send" when message.TryGetProperty("text", out var text):
                EditTruncate(message);
                Submit(
                    text.GetString() ?? string.Empty,
                    message.TryGetProperty("to", out var to) ? to.GetString() ?? string.Empty : string.Empty);
                break;

            case "stop":
                _turn?.Cancel();
                _approval?.TrySetResult('n');
                break;

            case "approve" when message.TryGetProperty("answer", out var answer):
                var choice = answer.GetString();
                _approval?.TrySetResult(choice is { Length: > 0 } ? choice[0] : 'n');
                _approval = null;
                break;

            case "web" when message.TryGetProperty("query", out var query):
                OpenWebSearch(query.GetString() ?? string.Empty);
                break;

            case "loadEarlier":
                LoadEarlier();
                break;

            case "command" when message.TryGetProperty("name", out var command)
                && command.GetString() == "compact":
                _ = RoomCompactAsync();
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
        }
    }

    // ---- attachments ----

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

    /// <summary>Attaches a saved SSH connection, the same way a one-to-one chat does.</summary>
    private void AttachSession()
    {
        var sessions = SessionCatalog.All;

        if (sessions.Count == 0)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatNoSsh")) });
            return;
        }

        var picker = new RedBloom.Views.SessionPickerDialog(sessions) { Owner = Window.GetWindow(this) };

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

    // ---- rendering ----

    /// <summary>How many turns are drawn at once — a page, so a long room opens fast.</summary>
    private const int PageSize = 40;

    /// <summary>How many of the most recent turns are currently drawn; grows a page at a time.</summary>
    private int _shown = PageSize;

    private void Greet()
    {
        Post(new { t = "avatar", src = string.Empty });

        // A room names who said what, so quoting a particular message is offered here.
        Post(new { t = "quoting", on = true });

        // A room can fold its own history down too — the "/compact" hint is offered the same way a
        // one-to-one chat offers it.
        Post(new { t = "commands", names = new[] { "compact" } });

        // The composer offers these as an @-mention picker, the way a group chat does.
        Post(new { t = "mentions", names = Participants().Select(a => a.Name) });

        RenderHistory();

        Post(new { t = "status", text = PolicyName(), busy = false });
    }

    /// <summary>
    /// Draws the most recent page of the room, newest first held back for scrolling.
    /// </summary>
    /// <remarks>
    /// Only the last <see cref="_shown"/> turns are sent, bracketed as one batch so the page draws
    /// them without following the tail per message — the two things that made a long room slow to
    /// open. Older turns are reached through the "show earlier" button, which grows the window by a
    /// page and redraws.
    /// </remarks>
    private void RenderHistory(bool loadingMore = false)
    {
        var turns = _room.Turns;
        var start = Math.Max(0, turns.Count - _shown);

        Post(new { t = "clear" });
        Post(new { t = "bulk", on = true });
        Post(new { t = "earlier", count = start });

        var names = string.Join(", ", Participants().Select(a => a.Name));
        Post(new { t = "note", html = Markdown.Escape($"{_room.Title} · {names}") });

        for (var i = start; i < turns.Count; i++)
        {
            var turn = turns[i];

            if (turn.Role == "user")
            {
                Post(new
                {
                    t = "user",
                    idx = i,
                    html = Markdown.ToHtml(turn.Text),
                    text = turn.Text,
                    files = turn.Attachments.Select(Attachments.Describe),
                });
            }
            else if (turn.Image.Length > 0)
            {
                Post(new { t = "image", label = turn.Speaker, src = ImageDataUri(turn.Image), path = turn.Image });
            }
            else
            {
                var agent = FindByName(turn.Speaker);
                Post(new
                {
                    t = "assistant",
                    label = turn.Speaker,
                    color = NickColor(agent),
                    avatar = AvatarDataUri(agent),
                    html = Markdown.ToHtml(turn.Text),
                });
                Post(new { t = "endTurn" });
            }
        }

        // The newest page lands at the bottom; loading older ones keeps the reader at the top of
        // what just appeared rather than throwing them to the end.
        Post(new { t = "bulk", on = false, scroll = loadingMore ? "top" : "bottom" });
    }

    /// <summary>Grows the shown window by a page and redraws, for the "show earlier" button.</summary>
    private void LoadEarlier()
    {
        if (_shown >= _room.Turns.Count)
        {
            return;
        }

        _shown = Math.Min(_room.Turns.Count, _shown + PageSize);
        RenderHistory(loadingMore: true);
    }

    /// <summary>
    /// Folds the earlier part of the room into a summary on "/compact", keeping the recent tail.
    /// </summary>
    /// <remarks>
    /// The per-message trim already keeps a send within the model, so this is the deliberate,
    /// lasting version: a participant is asked to summarise the older turns, and that stands in for
    /// them from then on. When the older part is itself too large for that participant to read in
    /// one pass, it is dropped rather than summarised — the same last resort the one-to-one chat
    /// takes.
    /// </remarks>
    private async Task RoomCompactAsync()
    {
        const int KeepVerbatim = 8;

        if (_turn is not null)
        {
            return;
        }

        if (_room.Turns.Count <= KeepVerbatim + 2)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatCompactNothing")) });
            return;
        }

        var summariser = Participants().FirstOrDefault(a => a.Provider != AiProvider.ImageGen);

        if (summariser is null)
        {
            Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_ChatCompactNothing")) });
            return;
        }

        var work = new CancellationTokenSource();
        _turn = work;
        Post(new { t = "status", text = LocalizationService.T("L_PhaseWrappingUp") + "…", busy = true });
        Post(new { t = "compact", state = "start" });

        try
        {
            var cut = _room.Turns.Count - KeepVerbatim;

            var older = new StringBuilder();
            for (var i = 0; i < cut; i++)
            {
                var turn = _room.Turns[i];
                var who = turn.Role == "user" ? LocalizationService.T("L_RoomUser") : turn.Speaker;
                older.Append(who).Append(": ").AppendLine(turn.Text);
            }

            var tail = _room.Turns.Skip(cut).ToList();

            // Too large to summarise in one pass on this participant — drop it rather than make a
            // call that would be refused. The recent tail is what carries the thread anyway.
            if (ChatContext.EstimateTokens([older.ToString()]) > summariser.ContextWindow * 0.8)
            {
                _room.Turns.RemoveRange(0, cut);
            }
            else
            {
                var summary = new StringBuilder();
                using var transport = AgentTransports.For(BareAgent(summariser));

                var ask = new List<AgentMessage>
                {
                    new(AgentRole.User,
                        "Summarise the group conversation below so it can stand in for the full text. "
                        + "Keep decisions, facts, names and anything still unresolved; note who said "
                        + "what where it matters. Write it as notes, not as a reply.\n\n" + older),
                };

                var drawn = 0;

                await foreach (var item in transport.SendAsync(ask, work.Token).ConfigureAwait(true))
                {
                    if (item.Kind == AgentEventKind.Text)
                    {
                        summary.Append(item.Text);

                        if (summary.Length - drawn >= 400)
                        {
                            drawn = summary.Length;
                            Post(new { t = "compact", state = "progress", chars = summary.Length });
                        }
                    }
                }

                if (summary.Length > 0)
                {
                    _room.Turns.Clear();
                    _room.Turns.Add(new ChatTurn
                    {
                        Role = "user",
                        Text = LocalizationService.T("L_RoomEarlierSummary") + "\n\n" + summary,
                    });
                    _room.Turns.AddRange(tail);
                }
            }

            _shown = PageSize;
            _room.Touch();
            RoomStore.Save(_room);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user; the room is left as it was.
        }
        catch (Exception ex)
        {
            Post(new { t = "note", html = Markdown.Escape(ex.Message) });
        }
        finally
        {
            Post(new { t = "compact", state = "done" });
            _turn = null;
            work.Dispose();
        }

        RenderHistory();
        Post(new { t = "status", text = PolicyName(), busy = false });
    }

    // ---- a user message and the round it starts ----

    /// <summary>
    /// When a message carries an edit position, drops that turn and everything after it before the
    /// edited text is sent, so an edit replaces the message rather than adding a duplicate.
    /// </summary>
    private void EditTruncate(JsonElement message)
    {
        if (_turn is not null
            || !message.TryGetProperty("editIndex", out var slot)
            || !slot.TryGetInt32(out var at)
            || at < 0
            || at >= _room.Turns.Count)
        {
            return;
        }

        _room.Turns.RemoveRange(at, _room.Turns.Count - at);
        _shown = PageSize;
        _room.Touch();
        RoomStore.Save(_room);
        RenderHistory();
    }

    private void Submit(string text, string to)
    {
        var question = text.Trim();

        if (question.Length == 0 || _turn is not null)
        {
            return;
        }

        // The attachments travel with the message they were pinned for, then the composer clears —
        // a pin left in place would silently re-send the file on every later message.
        var turn = new ChatTurn { Role = "user", Text = question, Attachments = [.. _pending] };
        _pending.Clear();

        _room.Turns.Add(turn);
        Post(new
        {
            t = "user",
            idx = _room.Turns.Count - 1,
            html = Markdown.ToHtml(question),
            text = question,
            files = turn.Attachments.Select(Attachments.Describe),
        });
        PushPending();

        _turn = new CancellationTokenSource();
        _ = RunRoundAsync(question, to, _turn.Token);
    }

    private async Task RunRoundAsync(string trigger, string to, CancellationToken cancellationToken)
    {
        try
        {
            var participants = Participants();

            if (participants.Count == 0)
            {
                Post(new { t = "note", html = Markdown.Escape(LocalizationService.T("L_RoomEmpty")) });
                return;
            }

            // Who the message that opened the round hands the floor to. A pinned target, then a
            // user @-mention, force a speaker in every policy; otherwise the room's rule decides.
            var queue = new Queue<AiAgent>(OpeningSpeakers(participants, trigger, to));
            var spoken = 0;

            while (queue.Count > 0 && spoken < MaxAgentTurnsPerMessage)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var agent = queue.Dequeue();
                var reply = await SpeakAsync(agent, participants, cancellationToken).ConfigureAwait(true);
                spoken++;

                // An agent that names another with an @ hands it the floor next — this is how one
                // agent reaches another, the image agent included.
                foreach (var mentioned in MentionedIn(reply, participants))
                {
                    if (mentioned.Id != agent.Id && !queue.Contains(mentioned))
                    {
                        queue.Enqueue(mentioned);
                    }
                }

                // In moderator mode the queue is empty by design; the moderator is asked again for
                // the next speaker until it declines or the cap is reached.
                if (_room.Policy == RoomPolicy.Moderator && queue.Count == 0)
                {
                    if (await ModeratorPickAsync(participants, cancellationToken).ConfigureAwait(true) is { } next)
                    {
                        queue.Enqueue(next);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user; whatever was said stays.
        }
        catch (Exception ex)
        {
            Post(new { t = "note", html = Markdown.Escape(ex.Message) });
        }
        finally
        {
            _room.Touch();
            RoomStore.Save(_room);
            Post(new { t = "status", text = PolicyName(), busy = false });
            _turn = null;
        }
    }

    /// <summary>Who answers first, before any agent hands the floor on.</summary>
    private List<AiAgent> OpeningSpeakers(List<AiAgent> participants, string trigger, string to)
    {
        // A "@name" typed into the message is a one-off: it wins for this message even over a pin,
        // so the user can address someone else once without releasing the target they set.
        var forced = MentionedIn(trigger, participants);

        if (forced.Count > 0)
        {
            return forced;
        }

        // The pinned target otherwise holds, whatever the room's turn-taking rule is, until the
        // user releases it.
        if (!string.IsNullOrWhiteSpace(to)
            && participants.FirstOrDefault(a => string.Equals(a.Name, to.Trim(), StringComparison.OrdinalIgnoreCase)) is { } pinned)
        {
            return [pinned];
        }

        switch (_room.Policy)
        {
            case RoomPolicy.All:
                return participants;

            case RoomPolicy.RoundRobin:
                var index = ((_room.Rotation % participants.Count) + participants.Count) % participants.Count;
                _room.Rotation = index + 1;
                return [participants[index]];

            case RoomPolicy.Moderator:
                // The moderator itself is asked who begins, so nothing here.
                return [];

            case RoomPolicy.Mention:
            default:
                // Nobody was named, so nobody speaks; the user drives with an @-mention.
                return [];
        }
    }

    /// <summary>Runs one agent and shows its reply under its own name; returns what it said.</summary>
    private async Task<string> SpeakAsync(AiAgent agent, List<AiAgent> participants, CancellationToken cancellationToken)
    {
        Post(new { t = "thinking", on = true, label = agent.Name, what = string.Empty });

        // An image participant draws from the conversation's latest request rather than chatting.
        if (agent.Provider == AiProvider.ImageGen)
        {
            return await DrawAsync(agent, cancellationToken).ConfigureAwait(true);
        }

        // The tool host is handed over only when the room permits commands; without it the agents
        // answer in words alone, exactly as before.
        _speaking = agent;
        using var transport = AgentTransports.For(RoomAgent(agent, participants), _room.AllowCommands ? this : null);
        var from = FittingStart(agent);
        var conversation = new List<AgentMessage> { new(AgentRole.User, Transcript(agent, from), RoomImages(from)) };

        var reply = new StringBuilder();

        await foreach (var item in transport.SendAsync(conversation, cancellationToken).ConfigureAwait(true))
        {
            switch (item.Kind)
            {
                case AgentEventKind.Text:
                    reply.Append(item.Text);
                    Post(new
                    {
                        t = "assistant",
                        label = agent.Name,
                        color = NickColor(agent),
                        avatar = AvatarDataUri(agent),
                        html = Markdown.ToHtml(reply.ToString()),
                    });
                    break;

                case AgentEventKind.ToolCall:
                    // The reply so far is committed before the command, so the two do not interleave.
                    if (reply.Length > 0)
                    {
                        Post(new { t = "endTurn" });
                        reply.Clear();
                    }

                    _activity++;
                    var (label, what) = Describe(item.Text);
                    Post(new { t = "activity", id = _activity.ToString(), state = "running", label, what });
                    break;

                case AgentEventKind.ToolResult:
                    Post(new { t = "activityDone", id = _activity.ToString(), summary = Summarise(item.Text), output = item.Text });
                    break;

                case AgentEventKind.ToolRefused:
                    Post(new { t = "activityDone", id = _activity.ToString(), summary = "skipped", output = "" });
                    break;

                case AgentEventKind.Failed:
                    Post(new { t = "note", html = Markdown.Escape($"{agent.Name}: {item.Text}") });
                    break;
            }
        }

        Post(new { t = "endTurn" });

        var text = reply.ToString().Trim();

        if (text.Length > 0)
        {
            _room.Turns.Add(new ChatTurn { Role = "assistant", Speaker = agent.Name, Text = text });
        }

        return text;
    }

    private async Task<string> DrawAsync(AiAgent agent, CancellationToken cancellationToken)
    {
        // The prompt is the newest thing said. The @ that pointed here is dropped, but the words
        // are kept — nothing is stripped beyond the marker itself.
        var last = _room.Turns.LastOrDefault(turn => turn.Text.Length > 0);
        var prompt = StripMentions(last?.Text ?? string.Empty);

        var options = new ImageOptions { ModelPath = ImageGen.ResolveModel(agent.Model) ?? string.Empty };
        var result = await ImageGen.GenerateAsync(prompt, options, cancellationToken).ConfigureAwait(true);

        Post(new { t = "endTurn" });

        if (!result.Ok || result.PngPath is null)
        {
            Post(new { t = "note", html = Markdown.Escape($"{agent.Name}: {result.Message}") });
            return string.Empty;
        }

        AgentFiles.Touched(result.PngPath);
        Post(new { t = "image", label = agent.Name, src = ImageDataUri(result.PngPath), path = result.PngPath });
        _room.Turns.Add(new ChatTurn { Role = "assistant", Speaker = agent.Name, Text = $"[picture: {prompt}]", Image = result.PngPath });

        return string.Empty;
    }

    /// <summary>Asks the moderator who should speak next; null when it declines or cannot be reached.</summary>
    private async Task<AiAgent?> ModeratorPickAsync(List<AiAgent> participants, CancellationToken cancellationToken)
    {
        var moderator = participants.FirstOrDefault(a => a.Id == _room.ModeratorId) ?? participants[0];
        var others = participants.Where(a => a.Id != moderator.Id).ToList();

        if (others.Count == 0)
        {
            return null;
        }

        var names = string.Join(", ", others.Select(a => a.Name));
        var ask =
            "You are directing a group chat. Read the conversation and reply with the single name of "
            + "the participant who should speak next, exactly as written here: " + names + ". Reply "
            + "with just the name, or the word DONE if the exchange is complete.\n\n"
            + Transcript(moderator, FittingStart(moderator));

        using var transport = AgentTransports.For(BareAgent(moderator));
        var conversation = new List<AgentMessage> { new(AgentRole.User, ask) };

        var answer = new StringBuilder();

        await foreach (var item in transport.SendAsync(conversation, cancellationToken).ConfigureAwait(true))
        {
            if (item.Kind == AgentEventKind.Text)
            {
                answer.Append(item.Text);
            }
        }

        var said = answer.ToString().Trim();

        if (said.Length == 0 || said.Contains("DONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return others.FirstOrDefault(a => said.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ---- transcript and agent shaping ----

    /// <summary>
    /// The conversation as one labelled block, told to the given agent.
    /// </summary>
    /// <remarks>
    /// Handed as a single user message rather than as alternating turns: a room mixes several
    /// speakers, and mapping that onto the strict user/assistant alternation both wire formats and
    /// local models expect is fragile. A labelled transcript sidesteps it and reads the same to
    /// every model — its own lines are marked as its own.
    /// </remarks>
    private string Transcript(AiAgent self, int from)
    {
        var text = new StringBuilder();

        for (var i = Math.Max(0, from); i < _room.Turns.Count; i++)
        {
            var turn = _room.Turns[i];

            var who = turn.Role == "user"
                ? LocalizationService.T("L_RoomUser")
                : turn.Speaker == self.Name ? $"{turn.Speaker} (you)" : turn.Speaker;

            text.Append(who).Append(": ").AppendLine(turn.Text);

            // A user turn's attachments — file contents, folder listings, an SSH connection's
            // details — are folded in under the line they were sent with, so the models actually
            // read what was attached rather than only being told a file exists.
            if (turn.Role == "user"
                && turn.Attachments.Count > 0
                && ChatContext.Build(turn.Attachments) is { } context)
            {
                text.AppendLine(context);
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The pictures attached in the window of turns being sent, for models that see them.</summary>
    private IReadOnlyList<AgentImage> RoomImages(int from)
    {
        var images = new List<AgentImage>();

        for (var i = Math.Max(0, from); i < _room.Turns.Count; i++)
        {
            var turn = _room.Turns[i];

            if (turn.Role == "user" && turn.Attachments.Count > 0)
            {
                images.AddRange(ChatContext.Images(turn.Attachments));
            }
        }

        return images;
    }

    /// <summary>
    /// The first turn to include so what is sent fits the agent's context.
    /// </summary>
    /// <remarks>
    /// The whole room as one message overruns a small local model — the reason a long room was
    /// refused outright. Only the newest turns that fit are sent, the persona and preamble taken off
    /// the budget first; nothing is deleted, so the older turns are still there to scroll back to
    /// and come back into range once the recent ones are folded away.
    /// </remarks>
    private int FittingStart(AiAgent self)
    {
        if (_room.Turns.Count == 0)
        {
            return 0;
        }

        var reserved = ChatContext.EstimateTokens([self.Instructions]);
        var budget = Math.Max(512.0, (self.ContextWindow * 0.8) - reserved);

        var used = 0.0;
        var start = _room.Turns.Count - 1;   // the newest is always kept, even if it alone is large

        for (var i = _room.Turns.Count - 1; i >= 0; i--)
        {
            var cost = Cost(_room.Turns[i]);

            if (i < _room.Turns.Count - 1 && used + cost > budget)
            {
                break;
            }

            used += cost;
            start = i;
        }

        return start;

        static double Cost(ChatTurn turn) =>
            ChatContext.EstimateTokens([turn.Text])
            + (ChatContext.CountImages(turn.Attachments) * ChatContext.TokensPerImage)
            + 8;
    }

    /// <summary>A clone of an agent with a room preamble added, so it answers as one voice in a cast.</summary>
    private AiAgent RoomAgent(AiAgent agent, List<AiAgent> participants)
    {
        var clone = agent.Clone();
        var cast = string.Join(", ", participants.Where(a => a.Id != agent.Id).Select(a => a.Name));

        var preamble =
            $"You are \"{agent.Name}\" in a group chat with: {cast}. The transcript labels each "
            + "line with who said it; lines marked \"(you)\" are your own. Reply only as "
            + $"{agent.Name}, in the first person, with a single message. Do not write other "
            + "participants' lines or prefix your reply with your name. To hand the floor to "
            + "another participant, mention them with an @ before their name.";

        clone.SystemPrompt = string.IsNullOrWhiteSpace(clone.SystemPrompt)
            ? preamble
            : clone.SystemPrompt.Trim() + "\n\n" + preamble;

        return clone;
    }

    /// <summary>A clone with no room preamble, for the moderator's routing question.</summary>
    private static AiAgent BareAgent(AiAgent agent)
    {
        var clone = agent.Clone();
        clone.IsRoleplay = false;
        return clone;
    }

    // ---- mentions ----

    /// <summary>The participants named with an @ in a message, in the order they are listed.</summary>
    private static List<AiAgent> MentionedIn(string text, List<AiAgent> participants)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@'))
        {
            return [];
        }

        return [.. participants.Where(a =>
            text.Contains("@" + a.Name, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Drops only the @ marker, keeping the words, so the picture prompt reads plainly.</summary>
    private static string StripMentions(string text) => text.Replace("@", string.Empty, StringComparison.Ordinal).Trim();

    // ---- helpers ----

    private AiAgent? FindByName(string name) =>
        ThemeService.Settings.Agents.FirstOrDefault(a =>
            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string NickColor(AiAgent? agent) =>
        agent is not null && !string.IsNullOrWhiteSpace(agent.NameColor)
            ? agent.NameColor
            : ThemeService.Settings.Accent;

    private string PolicyName() => LocalizationService.T(_room.Policy switch
    {
        RoomPolicy.All => "L_RoomPolicyAll",
        RoomPolicy.RoundRobin => "L_RoomPolicyRoundRobin",
        RoomPolicy.Moderator => "L_RoomPolicyModerator",
        _ => "L_RoomPolicyMention",
    });

    private static string AvatarDataUri(AiAgent? agent)
    {
        if (agent is null || string.IsNullOrWhiteSpace(agent.AvatarPath) || !File.Exists(agent.AvatarPath))
        {
            return string.Empty;
        }

        try
        {
            if (new FileInfo(agent.AvatarPath).Length > 4 * 1024 * 1024)
            {
                return string.Empty;
            }

            var media = Path.GetExtension(agent.AvatarPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };

            return $"data:{media};base64,{Convert.ToBase64String(File.ReadAllBytes(agent.AvatarPath))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

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

    private void PushStrings()
    {
        string[] keys =
        [
            "L_ChatAsk", "L_ChatSend", "L_ChatStop", "L_ChatCopied", "L_ChatReasoning",
            "L_ChatCopy", "L_ChatRepeat", "L_ChatEdit", "L_ChatThink",
            "L_ChatDownload", "L_ChatCopyImage", "L_ChatOpenExternal", "L_ChatClose",
            "L_ChatPrev", "L_ChatNext",
            "L_ChatCtxCopy", "L_ChatCtxWeb", "L_ChatCtxAsk", "L_ChatCtxAskOther",
            "L_ChatQuote", "L_ChatQuoteMe",
            "L_ChatMentionAll", "L_ChatMentionAllTitle", "L_ChatEarlier",
            "L_ChatEditing", "L_ChatCancelEdit", "L_ChatCompacting",
            "L_ChatCmdCompact", "L_ChatCmdCompactHint",
        ];

        Post(new { t = "strings", s = keys.ToDictionary(key => key[6..], LocalizationService.T) });
    }

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
                ["page"] = "transparent",
                ["bar"] = $"rgba({tint.R},{tint.G},{tint.B},0.45)",
                ["bubble"] = $"rgba({raised.R},{raised.G},{raised.B},0.45)",
                ["bubble-user"] = $"rgba({accent.R},{accent.G},{accent.B},0.22)",
                ["nick"] = s.Accent,
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

    private void Post(object message)
    {
        if (!_pageReady || _disposed)
        {
            return;
        }

        _webView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }

    /// <summary>Opens a web search for the selected text in the user's own browser.</summary>
    private static void OpenWebSearch(string query)
    {
        var wanted = query.Trim();

        if (wanted.Length > 0)
        {
            OpenExternally("https://www.google.com/search?q=" + Uri.EscapeDataString(wanted));
        }
    }

    private static void OpenExternally(string uri)
    {
        try
        {
            if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing registered to open it; there is nothing to do.
        }
    }

    // ---- commands (IAgentToolHost) ----

    /// <inheritdoc />
    /// <remarks>Gated on the room's own permission; a room with it off hands out no tool host at all.</remarks>
    public bool Enabled => _room.AllowCommands;

    /// <inheritdoc />
    /// <remarks>Off: a room draws through its image participants and @-mentions, not through tools.</remarks>
    public bool ImagesEnabled => false;

    /// <inheritdoc />
    public bool AgentsEnabled => false;

    /// <inheritdoc />
    public Task<bool> ApproveAsync(string command, bool elevated, CancellationToken cancellationToken)
    {
        var agent = _speaking;

        if (!elevated && agent is not null && (!_room.AskBeforeRun || agent.IsAlwaysAllowed(command)))
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
            var picked = await answer.ConfigureAwait(true);

            if (picked is 'a' or 'A' && _suggested.Length > 0 && agent is not null)
            {
                Remember(agent, _suggested);
                Post(new { t = "note", html = Markdown.Escape($"“{_suggested}” will run without asking from now on") });
            }

            return picked is 'y' or 'Y' or 'a' or 'A';
        }
    }

    /// <inheritdoc />
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
    public Task<string> ShareAsync(string path, string note, CancellationToken cancellationToken)
    {
        path = path.Trim().Trim('"');

        if (path.Length == 0 || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return Task.FromResult($"There is nothing at {path}, so it was not shared.");
        }

        var full = Path.GetFullPath(path);
        AgentFiles.Touched(full);

        Post(new
        {
            t = "shared",
            label = _speaking?.Name ?? string.Empty,
            note,
            files = new[] { Attachments.Describe(full) },
        });

        return Task.FromResult($"Shared {full} with the user; it is now in the room for them to open.");
    }

    /// <inheritdoc />
    /// <remarks>Not offered in a room; drawing is what an image participant is for.</remarks>
    public Task<string> GenerateImageAsync(string prompt, string negative, CancellationToken cancellationToken) =>
        Task.FromResult("Image generation is not available from a room tool; add an image agent as a participant instead.");

    /// <inheritdoc />
    /// <remarks>Not offered in a room; reaching another agent is what an @-mention is for.</remarks>
    public Task<string> AskAgentAsync(string agentName, string request, CancellationToken cancellationToken) =>
        Task.FromResult("Calling another agent is not available from a room tool; mention them with an @ instead.");

    /// <summary>Adds a standing command allowance to the running agent and the saved one behind it.</summary>
    private static void Remember(AiAgent agent, string pattern)
    {
        if (!agent.AllowedCommands.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            agent.AllowedCommands.Add(pattern);
        }

        var saved = ThemeService.Settings.Agents.FirstOrDefault(a => a.Id == agent.Id);

        if (saved is not null && !saved.AllowedCommands.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            saved.AllowedCommands.Add(pattern);
            ThemeService.Save();
        }
    }

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
            "del" or "erase" or "rm" or "rmdir" => ("deleting", rest),
            "git" => ("git", rest),
            "dotnet" or "npm" or "npx" or "pnpm" or "yarn" or "cargo" or "go" or "pip" => (program, rest),
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _turn?.Cancel();
        _turn?.Dispose();
        _approval?.TrySetResult('n');
        _webView.Dispose();
    }
}
