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
public sealed class RoomChatView : UserControl, IDisposable
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
                break;

            case "send" when message.TryGetProperty("text", out var text):
                Submit(
                    text.GetString() ?? string.Empty,
                    message.TryGetProperty("to", out var to) ? to.GetString() ?? string.Empty : string.Empty);
                break;

            case "stop":
                _turn?.Cancel();
                break;

            case "web" when message.TryGetProperty("query", out var query):
                OpenWebSearch(query.GetString() ?? string.Empty);
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

    private void Greet()
    {
        Post(new { t = "avatar", src = string.Empty });

        // A room names who said what, so quoting a particular message is offered here.
        Post(new { t = "quoting", on = true });

        // The composer offers these as an @-mention picker, the way a group chat does.
        Post(new { t = "mentions", names = Participants().Select(a => a.Name) });

        var names = string.Join(", ", Participants().Select(a => a.Name));
        Post(new { t = "note", html = Markdown.Escape($"{_room.Title} · {names}") });

        foreach (var turn in _room.Turns)
        {
            if (turn.Role == "user")
            {
                Post(new
                {
                    t = "user",
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

        Post(new { t = "status", text = PolicyName(), busy = false });
    }

    // ---- a user message and the round it starts ----

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

        using var transport = AgentTransports.For(RoomAgent(agent, participants));
        var conversation = new List<AgentMessage> { new(AgentRole.User, Transcript(agent), RoomImages()) };

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
            + "with just the name, or the word DONE if the exchange is complete.\n\n" + Transcript(moderator);

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
    private string Transcript(AiAgent self)
    {
        var text = new StringBuilder();

        foreach (var turn in _room.Turns)
        {
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

    /// <summary>Every picture attached across the user's turns, for the models that can see them.</summary>
    private IReadOnlyList<AgentImage> RoomImages()
    {
        var images = new List<AgentImage>();

        foreach (var turn in _room.Turns.Where(turn => turn.Role == "user" && turn.Attachments.Count > 0))
        {
            images.AddRange(ChatContext.Images(turn.Attachments));
        }

        return images;
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
            "L_ChatMentionAll", "L_ChatMentionAllTitle",
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _turn?.Cancel();
        _turn?.Dispose();
        _webView.Dispose();
    }
}
