using System.Text;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Terminal;

/// <summary>
/// Runs a configured AI agent as a terminal session, alongside the local shell and SSH ones.
/// </summary>
/// <remarks>
/// There is no process on the other end, so this backend is the line editor as well: xterm.js
/// sends raw keys and expects whatever should appear to be echoed back. A shell would normally
/// do that job, which is why the local and remote backends do not.
/// </remarks>
public sealed class AgentBackend : ITerminalBackend, IAgentToolHost
{
    private const string Reset = "\u001b[0m";
    private const string Accent = "\u001b[38;5;203m";
    private const string Faint = "\u001b[38;5;245m";

    private readonly AiAgent _agent;
    private readonly IAgentTransport _transport;
    private readonly List<AgentMessage> _history = [];
    private readonly StringBuilder _line = new();

    /// <summary>Cancels the turn in flight, so Ctrl+C interrupts a long answer.</summary>
    private CancellationTokenSource? _turn;

    private bool _busy;
    private bool _disposed;
    private int _closedRaised;

    /// <summary>Set while a command is waiting on the user's answer.</summary>
    private TaskCompletionSource<bool>? _approval;

    /// <summary>What an "always allow" answer would remember, shown in the question itself.</summary>
    private string _suggested = string.Empty;

    public AgentBackend(AiAgent agent)
    {
        _agent = agent;

        // The session is its own tool host: only it knows how to put the question to the user.
        _transport = AgentTransports.For(agent, this);
    }

    public event Action<string>? Output;
    public event Action<string>? Closed;

    public bool IsRunning => !_disposed;

    public Task StartAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var thinking = _agent.Provider == AiProvider.Anthropic
            ? $", thinking {(_agent.Thinking ? "adaptive" : "off")}, effort {_agent.Effort}"
            : string.Empty;

        Emit($"{Accent}{_agent.Name}{Reset} — {_agent.Model}{thinking}\r\n");
        Emit($"{Faint}{_agent.ResolvedBaseUrl}{Reset}\r\n");
        if (_agent.AllowCommands)
        {
            Emit($"{Faint}can run commands{(_agent.AskBeforeRun ? " (each one asks first)" : " without asking")}{Reset}\r\n");
        }

        Emit($"{Faint}/help for commands, Ctrl+C to interrupt, /exit to close.{Reset}\r\n\r\n");
        Prompt();

        return Task.CompletedTask;
    }

    /// <summary>Takes raw key input from the terminal and echoes what belongs on screen.</summary>
    public void Write(string data)
    {
        if (_disposed || string.IsNullOrEmpty(data))
        {
            return;
        }

        foreach (var ch in data)
        {
            // An approval question owns the keyboard until it is answered, so the usual line
            // editing is bypassed for as long as one is open.
            if (_approval is not null)
            {
                Answer(ch);
                continue;
            }

            switch (ch)
            {
                case '\u0003': // Ctrl+C
                    Interrupt();
                    break;

                case '\r':
                case '\n':
                    if (!_busy)
                    {
                        Submit();
                    }

                    break;

                case '\u007f': // Backspace arrives as DEL from xterm.js
                case '\b':
                    if (!_busy && _line.Length > 0)
                    {
                        _line.Length--;

                        // Step back, overwrite with a space, step back again — the terminal has
                        // no notion of deleting, only of drawing.
                        Emit("\b \b");
                    }

                    break;

                default:
                    // Control characters other than the ones handled above would corrupt the
                    // line, so only printable input is taken.
                    if (!_busy && !char.IsControl(ch))
                    {
                        _line.Append(ch);
                        Emit(ch.ToString());
                    }

                    break;
            }
        }
    }

    /// <summary>Nothing to tell: the far end is an HTTP endpoint with no idea of a viewport.</summary>
    public void Resize(int columns, int rows)
    {
    }

    // ---- running commands for the agent ----

    /// <inheritdoc />
    public bool Enabled => _agent.AllowCommands;

    /// <inheritdoc />
    /// <remarks>
    /// The question is deliberately the terminal's own, answered with a keypress in the session
    /// the command would run from — not a dialog somewhere else. Anything other than an explicit
    /// yes is a no, including Enter, so a distracted keypress cannot approve a command.
    /// </remarks>
    public Task<bool> ApproveAsync(string command, CancellationToken cancellationToken)
    {
        if (!_agent.AskBeforeRun)
        {
            return Task.FromResult(true);
        }

        if (_agent.IsAlwaysAllowed(command))
        {
            Emit($"{Faint}allowed{Reset}\r\n");
            return Task.FromResult(true);
        }

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approval = pending;

        // The pattern is spelled out rather than left to be guessed: "always allow" is only a
        // safe thing to offer if the user can see exactly how wide the allowance is.
        _suggested = AiAgent.SuggestAllowPattern(command);
        Emit($"{Faint}run it? [y]es / [N]o / [a]lways allow \"{_suggested}\"{Reset} ");

        // An interrupted turn must not leave the question hanging.
        cancellationToken.Register(() =>
        {
            _approval = null;
            pending.TrySetResult(false);
        });

        return pending.Task;
    }

    /// <inheritdoc />
    public Task<string> RunAsync(string command, CancellationToken cancellationToken) =>
        CommandRunner.RunAsync(command, cancellationToken);

    private void Answer(char ch)
    {
        if (_approval is not { } pending)
        {
            return;
        }

        var always = ch is 'a' or 'A';
        var yes = always || ch is 'y' or 'Y';

        if (always && _suggested.Length > 0)
        {
            Remember(_suggested);
            Emit($"a\r\n{Faint}\"{_suggested}\" will run without asking from now on{Reset}\r\n");
        }
        else
        {
            Emit(yes ? "y\r\n" : "n\r\n");
        }

        _approval = null;
        pending.TrySetResult(yes);
    }

    /// <summary>
    /// Adds a pattern to this session and to the saved agent behind it.
    /// </summary>
    /// <remarks>
    /// A session runs on a copy of its agent, so writing to the copy alone would last only until
    /// the tab closed. The saved one is found by id — a rename cannot lose it — and the settings
    /// file is written straight away, because an allowance the user granted must survive a
    /// crash as reliably as one they typed into the page.
    /// </remarks>
    private void Remember(string pattern)
    {
        if (!_agent.AllowedCommands.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            _agent.AllowedCommands.Add(pattern);
        }

        var saved = ThemeService.Settings.Agents.FirstOrDefault(a => a.Id == _agent.Id);

        if (saved is null)
        {
            return;
        }

        if (!saved.AllowedCommands.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            saved.AllowedCommands.Add(pattern);
        }

        ThemeService.Save();
    }

    private void Prompt() => Emit($"{Accent}❯{Reset} ");

    private void Interrupt()
    {
        if (_busy && _turn is { } turn)
        {
            turn.Cancel();
            return;
        }

        // Idle: abandon whatever is half-typed, exactly as a shell does.
        _line.Clear();
        Emit("^C\r\n");
        Prompt();
    }

    private void Submit()
    {
        var text = _line.ToString().Trim();
        _line.Clear();
        Emit("\r\n");

        if (text.Length == 0)
        {
            Prompt();
            return;
        }

        if (text.StartsWith('/'))
        {
            RunCommand(text);
            return;
        }

        _ = SendAsync(text);
    }

    private void RunCommand(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "/exit":
            case "/quit":
                Emit($"{Faint}Session closed.{Reset}\r\n");
                Raise(Closed, "The agent session was closed.");
                return;

            case "/clear":
                // Clear the screen and park the cursor at the top left.
                Emit("\u001b[2J\u001b[H");
                break;

            case "/reset":
                _history.Clear();
                Emit($"{Faint}History cleared — the next message starts a new conversation.{Reset}\r\n");
                break;

            case "/help":
                Emit($"{Faint}/reset  forget the conversation so far{Reset}\r\n");
                Emit($"{Faint}/clear  clear the screen, keep the conversation{Reset}\r\n");
                Emit($"{Faint}/exit   close this session{Reset}\r\n");
                break;

            default:
                Emit($"{Faint}Unknown command '{command}'. Try /help.{Reset}\r\n");
                break;
        }

        Prompt();
    }

    private async Task SendAsync(string text)
    {
        _busy = true;
        _history.Add(new AgentMessage(AgentRole.User, text));

        var turn = new CancellationTokenSource();
        _turn = turn;

        var reply = new StringBuilder();
        var thinkingShown = false;

        try
        {
            await foreach (var item in _transport.SendAsync(_history, turn.Token).ConfigureAwait(false))
            {
                switch (item.Kind)
                {
                    case AgentEventKind.Thinking:
                        Emit($"{Faint}thinking…{Reset}\r\n");
                        thinkingShown = true;
                        break;

                    case AgentEventKind.Text:
                        if (thinkingShown)
                        {
                            // Drop the placeholder line now that real text is arriving.
                            Emit("\u001b[1A\u001b[2K\r");
                            thinkingShown = false;
                        }

                        reply.Append(item.Text);
                        Emit(ToTerminal(item.Text));
                        break;

                    case AgentEventKind.ToolCall:
                        if (thinkingShown)
                        {
                            Emit("[1A[2K\r");
                            thinkingShown = false;
                        }

                        Emit($"\r\n{Accent}${Reset} {item.Text}\r\n");
                        break;

                    case AgentEventKind.ToolResult:
                        Emit($"{Faint}{ToTerminal(Shorten(item.Text))}{Reset}\r\n");
                        break;

                    case AgentEventKind.ToolRefused:
                        Emit($"{Faint}skipped{Reset}\r\n");
                        break;

                    case AgentEventKind.Failed:
                        Emit($"\r\n{Accent}{item.Text}{Reset}\r\n");
                        break;

                    case AgentEventKind.Completed:
                        if (item.Text.Length > 0)
                        {
                            Emit($"{Faint}{item.Text}{Reset}\r\n");
                        }

                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit($"\r\n{Accent}{ex.Message}{Reset}\r\n");
        }
        finally
        {
            _turn = null;
            turn.Dispose();
            _busy = false;
        }

        if (reply.Length > 0)
        {
            _history.Add(new AgentMessage(AgentRole.Assistant, reply.ToString()));
        }
        else
        {
            // Nothing came back, so the user turn is dropped too — leaving it would send the
            // same unanswered question again as history on the next turn.
            _history.RemoveAt(_history.Count - 1);
        }

        Emit("\r\n");
        Prompt();
    }

    /// <summary>
    /// Model text uses bare newlines; a terminal needs a carriage return with each one or the
    /// next line starts wherever the last one ended.
    /// </summary>
    /// <summary>
    /// Trims command output for the screen. The model still receives the whole thing; this is
    /// only so a command that prints thousands of lines does not bury the conversation.
    /// </summary>
    private static string Shorten(string text)
    {
        const int MaxLines = 25;
        var lines = text.Split('\n');

        return lines.Length <= MaxLines
            ? text.TrimEnd()
            : string.Join('\n', lines.Take(MaxLines)).TrimEnd()
              + $"\n… {lines.Length - MaxLines} more lines (the agent sees all of it)";
    }

    private static string ToTerminal(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    private void Emit(string text) => Output?.Invoke(text);

    private void Raise(Action<string>? handler, string reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            handler?.Invoke(reason);
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
        _transport.Dispose();
    }
}
