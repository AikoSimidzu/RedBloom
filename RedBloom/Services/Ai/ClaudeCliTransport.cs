using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Runs the installed Claude Code command-line tool and reads its streamed output.
/// </summary>
/// <remarks>
/// The tool is asked for one answer at a time in print mode, with its machine-readable stream
/// turned on. Continuity is the tool's own: the first run returns a session id, and every later
/// run resumes it, so the conversation lives where the tool keeps it rather than being replayed
/// from here. That also means its tools, project awareness and permissions behave exactly as
/// they do in a terminal.
/// </remarks>
public sealed class ClaudeCliTransport : IAgentTransport
{
    private readonly AiAgent _agent;
    private string? _session;

    /// <summary>True once this turn's answer has arrived as fragments rather than in one piece.</summary>
    private bool _streamed;

    public ClaudeCliTransport(AiAgent agent) => _agent = agent;

    public async IAsyncEnumerable<AgentEvent> SendAsync(
        IReadOnlyList<AgentMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (ClaudeCli.Executable is not { } exe)
        {
            yield return AgentEvent.Failure(LocalizationService.T("L_CliMissing"));
            yield break;
        }

        _streamed = false;

        if (Prompt(conversation) is not { Length: > 0 } prompt)
        {
            yield return AgentEvent.Failure(LocalizationService.T("L_CliNothingToSay"));
            yield break;
        }

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // The input side needs saying too. Left alone it is the console's code page, which
            // on a Russian Windows is 866 — and a question written in Cyrillic reaches the tool
            // as a row of question marks, unrecoverably, because the conversion happens on the
            // way out of this process.
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        // The prompt arrives on the standard input rather than on the command line: it can be
        // thousands of characters with quotes and newlines in it, and a command line cannot
        // carry that intact on Windows.
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add("--output-format");
        start.ArgumentList.Add("stream-json");
        start.ArgumentList.Add("--verbose");

        // Asks for the fine-grained stream. Without it the tool reports only finished blocks —
        // the answer lands in one lump at the end, and the reasoning is not reported at all.
        start.ArgumentList.Add("--include-partial-messages");

        if (_session is { Length: > 0 } resume)
        {
            start.ArgumentList.Add("--resume");
            start.ArgumentList.Add(resume);
        }

        if (!string.IsNullOrWhiteSpace(_agent.Model))
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(_agent.Model);
        }

        // Starting sits in a helper: C# forbids yielding from inside a catch, and a failure to
        // start has to reach the caller as an event rather than as an exception.
        var (process, refused) = Launch(start, exe);

        if (refused is not null || process is null)
        {
            yield return AgentEvent.Failure(refused ?? string.Format(LocalizationService.T("L_CliNoStart"), exe));
            yield break;
        }

        using (process)
        {
            await using (process.StandardInput)
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            var said = false;

            await foreach (var item in ReadAsync(process, cancellationToken).ConfigureAwait(false))
            {
                if (item.Kind == AgentEventKind.Text)
                {
                    said = true;
                }

                yield return item;
            }

            var complaint = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (!said)
            {
                yield return AgentEvent.Failure(complaint.Trim() is { Length: > 0 } why
                    ? why
                    : LocalizationService.T("L_CliNoAnswer"));

                yield break;
            }

            yield return new AgentEvent(AgentEventKind.Completed, string.Empty);
        }
    }

    private static (Process? Process, string? Error) Launch(ProcessStartInfo start, string exe)
    {
        try
        {
            return (Process.Start(start), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (null, string.Format(LocalizationService.T("L_CliNoStart"), ex.Message));
        }
    }

    /// <summary>
    /// Reads the tool's stream, one JSON object per line.
    /// </summary>
    /// <remarks>
    /// Parsing sits in its own method because a malformed line must not end the turn: the tool
    /// prints diagnostics on the same stream from time to time, and skipping what does not parse
    /// costs nothing.
    /// </remarks>
    private async IAsyncEnumerable<AgentEvent> ReadAsync(
        Process process, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            foreach (var item in Interpret(line))
            {
                yield return item;
            }
        }
    }

    private List<AgentEvent> Interpret(string line)
    {
        var events = new List<AgentEvent>();

        if (line.Length == 0 || line[0] != '{')
        {
            return events;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            // Every event carries the session; keeping the latest is what lets the next turn
            // resume rather than start over.
            if (root.TryGetProperty("session_id", out var session)
                && session.ValueKind == JsonValueKind.String)
            {
                _session = session.GetString();
            }

            var kind = root.TryGetProperty("type", out var type) ? type.GetString() : null;

            // The fine-grained stream: the answer arrives a fragment at a time, and the
            // reasoning arrives beside it under a delta of its own.
            if (kind == "stream_event"
                && root.TryGetProperty("event", out var raw)
                && raw.TryGetProperty("type", out var rawKind)
                && rawKind.GetString() == "content_block_delta"
                && raw.TryGetProperty("delta", out var delta))
            {
                var shape = delta.TryGetProperty("type", out var deltaKind) ? deltaKind.GetString() : null;

                if (shape == "text_delta" && delta.TryGetProperty("text", out var piece)
                    && piece.GetString() is { Length: > 0 } fragment)
                {
                    _streamed = true;
                    events.Add(AgentEvent.OfText(fragment));
                }

                if (shape == "thinking_delta" && delta.TryGetProperty("thinking", out var thought)
                    && thought.GetString() is { Length: > 0 } reasoning)
                {
                    events.Add(new AgentEvent(AgentEventKind.Thinking, reasoning));
                }

                return events;
            }

            // The finished block. Skipped when the fragments already carried it, which is the
            // usual case — kept as the way through for a version that does not stream them.
            if (kind == "assistant" && _streamed)
            {
                return events;
            }

            if (kind == "assistant"
                && root.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var blockKind = block.TryGetProperty("type", out var named) ? named.GetString() : null;

                    if (blockKind == "text"
                        && block.TryGetProperty("text", out var text)
                        && text.GetString() is { Length: > 0 } said)
                    {
                        events.Add(AgentEvent.OfText(said));
                    }

                    // The tool returns its reasoning as a block of its own, which is folded away
                    // rather than mixed into the answer.
                    if (blockKind == "thinking"
                        && block.TryGetProperty("thinking", out var thought)
                        && thought.GetString() is { Length: > 0 } reasoning)
                    {
                        events.Add(new AgentEvent(AgentEventKind.Thinking, reasoning));
                    }
                }
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                events.Add(AgentEvent.Spent(Count(usage, "input_tokens"), Count(usage, "output_tokens")));
            }

            if (kind == "system" && root.TryGetProperty("subtype", out var subtype)
                && subtype.GetString() == "init")
            {
                events.Add(AgentEvent.Doing(AgentPhase.Thinking));
            }
        }
        catch (JsonException)
        {
            // Not one of the tool's events; nothing to report.
        }

        return events;

        static long Count(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : 0;
    }

    /// <summary>
    /// What to send this turn: only the newest question once the tool is holding the thread.
    /// </summary>
    /// <remarks>
    /// Replaying the whole conversation into a resumed session would say everything twice. On
    /// the first turn there is no session yet, so the earlier turns go in as context.
    /// </remarks>
    private string Prompt(IReadOnlyList<AgentMessage> conversation)
    {
        var last = conversation.LastOrDefault(m => m.Role == AgentRole.User);

        if (_session is { Length: > 0 })
        {
            return last.Text;
        }

        var text = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(_agent.Instructions))
        {
            text.AppendLine(_agent.Instructions).AppendLine();
        }

        foreach (var message in conversation)
        {
            if (ReferenceEquals(message.Text, last.Text) && message.Role == AgentRole.User)
            {
                continue;
            }

            text.Append(message.Role == AgentRole.User ? "User: " : "Assistant: ")
                .AppendLine(message.Text)
                .AppendLine();
        }

        return text.Append(last.Text).ToString();
    }

    /// <summary>Checks that the tool is installed and signed in, without spending a turn.</summary>
    public async Task<string?> TestAsync(CancellationToken cancellationToken = default)
    {
        if (ClaudeCli.Executable is not { } exe)
        {
            return LocalizationService.T("L_CliMissing");
        }

        try
        {
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("--version");

            using var process = Process.Start(start);

            if (process is null)
            {
                return string.Format(LocalizationService.T("L_CliNoStart"), exe);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 ? null : LocalizationService.T("L_CliNotReady");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return string.Format(LocalizationService.T("L_CliNoStart"), ex.Message);
        }
    }

    public void Dispose()
    {
        // Nothing is held between turns but the session id, which is a string.
    }
}
