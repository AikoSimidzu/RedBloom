using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Talks to any endpoint that implements the <c>/v1/chat/completions</c> shape — proxies, local
/// runners, and third-party providers.
/// </summary>
/// <remarks>
/// Written against the wire format directly rather than through a vendor SDK, because the point
/// of this transport is the endpoints that are merely shaped like one provider without being it.
/// Effort and thinking have no place in this format and are ignored; the settings page hides
/// them for agents on this provider rather than pretending they apply.
/// </remarks>
public sealed class OpenAiCompatibleTransport : IAgentTransport
{
    /// <summary>How many command rounds one turn may take before it is called a loop.</summary>
    /// <inheritdoc cref="AnthropicTransport.MaxToolSteps" />
    private const int MaxToolSteps = 40;

    /// <summary>
    /// Endpoint-and-model pairs that answered a tool request with "does not support tools".
    /// </summary>
    /// <remarks>
    /// Remembered across the whole app, not just one turn: many local models are served without a
    /// tools template, and once one has rejected tools there is no point offering them again on
    /// every later turn only to have the whole request thrown out. Keyed by endpoint and model
    /// together, because the same model name behind two servers is not the same deployment.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, byte> Toolless = new();

    private readonly AiAgent _agent;
    private readonly IAgentToolHost? _tools;
    private readonly HttpClient _http;

    public OpenAiCompatibleTransport(AiAgent agent, IAgentToolHost? tools = null)
    {
        _agent = agent;
        _tools = tools;

        // No overall timeout: a streamed answer legitimately takes minutes, and HttpClient's
        // default would abort it mid-reply. Cancellation is the caller's to drive.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        if (!string.IsNullOrWhiteSpace(agent.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.ApiKey);
        }
    }

    /// <summary>Whether the agent asks for any tool at all.</summary>
    private bool ToolsWanted =>
        _tools is not null && (_tools.Enabled || _tools.ImagesEnabled || _tools.AgentsEnabled || _tools.TasksEnabled);

    /// <summary>The key this endpoint-and-model pair is remembered as toolless under.</summary>
    private string ToolKey => _agent.ResolvedBaseUrl + "\n" + _agent.Model;

    /// <summary>
    /// Whether to take the tool-running path: the agent wants tools, and this model has not already
    /// said it cannot do them.
    /// </summary>
    private bool ToolsEnabled => ToolsWanted && !Toolless.ContainsKey(ToolKey);

    public IAsyncEnumerable<AgentEvent> SendAsync(
        IReadOnlyList<AgentMessage> conversation,
        CancellationToken cancellationToken = default) =>
        ToolsEnabled
            ? SendWithToolsAsync(conversation, cancellationToken)
            : StreamAsync(conversation, cancellationToken);

    /// <summary>
    /// The turn as a request-and-run loop, used when the agent may execute commands.
    /// </summary>
    /// <remarks>
    /// Not streamed, for the same reason as the Anthropic side: a streamed tool call arrives as
    /// fragments of JSON that have to be stitched back together, while a whole response carries
    /// the arguments already parsed.
    /// </remarks>
    private async IAsyncEnumerable<AgentEvent> SendWithToolsAsync(
        IReadOnlyList<AgentMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(_agent.Instructions))
        {
            messages.Add(new { role = "system", content = _agent.Instructions });
        }

        foreach (var message in conversation)
        {
            messages.Add(ToMessage(message));
        }

        long spentIn = 0;
        long spentOut = 0;

        for (var step = 0; step < MaxToolSteps; step++)
        {
            yield return AgentEvent.Doing(step == 0 ? AgentPhase.Thinking : AgentPhase.Deciding);

            var (json, error, toolsUnsupported) = await PostAsync(messages, cancellationToken).ConfigureAwait(false);

            // This model cannot take tools. Remember it so later turns skip them outright, and carry
            // on for this one as a plain streamed chat rather than failing — the answer still comes,
            // only without the ability to run a command or draw.
            if (toolsUnsupported)
            {
                Toolless[ToolKey] = 1;

                await foreach (var item in StreamAsync(conversation, cancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }

                yield break;
            }

            if (error is not null || json is null)
            {
                yield return AgentEvent.Failure(error ?? "The endpoint returned nothing.");
                yield break;
            }

            var (roundIn, roundOut) = ReadUsage(json);
            spentIn += roundIn;
            spentOut += roundOut;

            if (spentIn > 0 || spentOut > 0)
            {
                yield return AgentEvent.Spent(spentIn, spentOut);
            }

            using var document = JsonDocument.Parse(json);

            if (!TryReadMessage(document.RootElement, out var reply))
            {
                yield return AgentEvent.Failure("The endpoint returned a reply in an unexpected shape.");
                yield break;
            }

            if (reply.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                yield return AgentEvent.OfText(text);
            }

            var calls = ReadToolCalls(reply);

            if (calls.Count == 0)
            {
                yield return new AgentEvent(AgentEventKind.Completed, string.Empty);
                yield break;
            }

            // The assistant turn goes back exactly as it arrived, tool calls and all: the ids in
            // it are what the results below are matched against.
            messages.Add(reply.Clone());

            foreach (var call in calls)
            {
                var id = call.Id;

                // Sharing opens nothing and runs nothing, so it is not put to the user.
                if (call.Name == AgentTransports.Share.Name)
                {
                    yield return AgentEvent.Doing(AgentPhase.Sharing);

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = id,
                        content = await _tools!.ShareAsync(call.Path, call.Note, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                // Drawing runs a diffusion model and shows the result; like sharing, it is not put
                // to the user for approval. The prompt rides in Command and the negative in Note.
                if (call.Name == AgentTransports.Image.Name)
                {
                    yield return AgentEvent.Doing(AgentPhase.Drawing);

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = id,
                        content = await _tools!.GenerateImageAsync(call.Command, call.Note, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                // Asking another agent runs it and returns what it made. The agent name rides in
                // Path and the request in Note.
                if (call.Name == AgentTransports.Ask.Name)
                {
                    yield return AgentEvent.Doing(AgentPhase.Asking);

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = id,
                        content = await _tools!.AskAgentAsync(call.Path, call.Note, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                // Keeping the task list runs nothing and needs no approval. Its whole arguments
                // object rides in Command, so the host can parse it the same way both transports do.
                if (call.Name == AgentTransports.Tasks.Name)
                {
                    yield return AgentEvent.Doing(AgentPhase.Tasks);

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = id,
                        content = await _tools!.ManageTasksAsync(call.Command, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                // The file tools carry their own approval inside the host; the arguments ride in
                // Command as they arrived.
                if (AgentTransports.Files.Names.Contains(call.Name))
                {
                    yield return AgentEvent.Doing(
                        call.Name is AgentTransports.Files.Write or AgentTransports.Files.Edit
                            ? AgentPhase.WritingFile
                            : AgentPhase.ReadingFile);

                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = id,
                        content = await _tools!.FileToolAsync(call.Name, call.Command, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                var command = call.Command;
                var elevated = call.Elevated;

                yield return new AgentEvent(AgentEventKind.ToolCall, command);

                var approved = await _tools!.ApproveAsync(command, elevated, cancellationToken).ConfigureAwait(false);
                string output;

                if (approved)
                {
                    yield return AgentEvent.Doing(elevated ? AgentPhase.RunningElevated : AgentPhase.Running);

                    output = await _tools.RunAsync(command, elevated, cancellationToken).ConfigureAwait(false);

                    yield return new AgentEvent(AgentEventKind.ToolResult, output);
                    yield return AgentEvent.Doing(AgentPhase.ReadingOutput);
                }
                else
                {
                    yield return new AgentEvent(AgentEventKind.ToolRefused, command);
                    output = "The user declined to run this command.";
                }

                messages.Add(new { role = "tool", tool_call_id = id, content = output });
            }
        }

        // Out of rounds. Asked once more with no tools offered at all, so the turn ends in a
        // report of what was done rather than in an error that throws all of it away.
        messages.Add(new { role = "user", content = AnthropicTransport.OutOfRounds });

        var (closing, failure, _) = await PostAsync(messages, cancellationToken, withTools: false)
            .ConfigureAwait(false);

        if (failure is not null || closing is null)
        {
            yield return AgentEvent.Failure(failure ?? "The endpoint returned nothing.");
            yield break;
        }

        using (var last = JsonDocument.Parse(closing))
        {
            if (TryReadMessage(last.RootElement, out var final)
                && final.TryGetProperty("content", out var closingText)
                && closingText.ValueKind == JsonValueKind.String
                && closingText.GetString() is { Length: > 0 } said)
            {
                yield return AgentEvent.OfText(said);
            }
        }

        yield return new AgentEvent(AgentEventKind.Completed, string.Empty);
    }

    private static bool TryReadMessage(JsonElement root, out JsonElement message)
    {
        message = default;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out message))
        {
            return false;
        }

        return true;
    }

    /// <summary>One tool call, whichever of the two tools it is for.</summary>
    private readonly record struct Call(
        string Id, string Name, string Command, bool Elevated, string Path, string Note);

    private static List<Call> ReadToolCalls(JsonElement message)
    {
        var calls = new List<Call>();

        if (!message.TryGetProperty("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var call in toolCalls.EnumerateArray())
        {
            if (!call.TryGetProperty("id", out var id)
                || !call.TryGetProperty("function", out var function)
                || !function.TryGetProperty("arguments", out var arguments))
            {
                continue;
            }

            var name = function.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String
                ? named.GetString() ?? string.Empty
                : string.Empty;

            try
            {
                // The specification says the arguments are a JSON string holding JSON, and most
                // endpoints send exactly that — but some proxies send the object itself. Both are
                // accepted: refusing the second shape loses every tool call the model makes.
                using var parsed = JsonDocument.Parse(
                    arguments.ValueKind == JsonValueKind.String
                        ? arguments.GetString() ?? "{}"
                        : arguments.GetRawText());

                var root = parsed.RootElement;

                if (name == AgentTransports.Share.Name)
                {
                    calls.Add(new Call(
                        Id(id),
                        name,
                        string.Empty,
                        false,
                        Text(root, AgentTransports.Share.Parameter),
                        Text(root, AgentTransports.Share.Note)));

                    continue;
                }

                // The prompt is carried in Command and the negative in Note, so the dispatch above
                // can pull them out without the record needing fields of its own for drawing.
                if (name == AgentTransports.Image.Name)
                {
                    calls.Add(new Call(
                        Id(id),
                        name,
                        Text(root, AgentTransports.Image.Parameter),
                        false,
                        string.Empty,
                        Text(root, AgentTransports.Image.Negative)));

                    continue;
                }

                // The agent name rides in Path and the request in Note, reusing the two string
                // slots the share tool already uses.
                if (name == AgentTransports.Ask.Name)
                {
                    calls.Add(new Call(
                        Id(id),
                        name,
                        string.Empty,
                        false,
                        Text(root, AgentTransports.Ask.Parameter),
                        Text(root, AgentTransports.Ask.Request)));

                    continue;
                }

                // The whole arguments object is carried in Command untouched, so the host parses the
                // task fields itself and nothing here needs to know their shape.
                if (name == AgentTransports.Tasks.Name || AgentTransports.Files.Names.Contains(name))
                {
                    calls.Add(new Call(Id(id), name, root.GetRawText(), false, string.Empty, string.Empty));

                    continue;
                }

                if (root.TryGetProperty(AgentTransports.Command.Parameter, out var command)
                    && command.ValueKind == JsonValueKind.String)
                {
                    var elevated = root
                        .TryGetProperty(AgentTransports.Command.Elevated, out var asAdmin)
                        && asAdmin.ValueKind == JsonValueKind.True;

                    calls.Add(new Call(
                        Id(id),
                        AgentTransports.Command.Name,
                        command.GetString() ?? string.Empty,
                        elevated,
                        string.Empty,
                        string.Empty));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A call whose arguments do not parse cannot be run; skipping it leaves the
                // model to notice the missing result and try again. Never thrown onwards: this
                // runs inside the turn, and one malformed call must not end the conversation.
            }
        }

        return calls;

        // Ids are strings in the specification and numbers at more than one gateway; both have
        // to come back out looking the same, because this is what results are matched against.
        static string Id(JsonElement id) =>
            id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : id.GetRawText();

        static string Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private async Task<(string? Json, string? Error, bool ToolsUnsupported)> PostAsync(
        List<object> messages, CancellationToken cancellationToken, bool withTools = true)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
            {
                Content = new StringContent(
                    BuildToolPayload(messages, withTools), Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (body, null, false);
            }

            // A model served without a tools template rejects the whole request the moment tools
            // are offered. Told apart from a real failure so the turn can be retried without them
            // rather than shown as an error, and reported up so the caller can stop offering them.
            if (withTools && ToolsRejected(body))
            {
                return (null, null, true);
            }

            return (null, Describe(response, body), false);
        }
        catch (OperationCanceledException)
        {
            return (null, null, false);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not reach {_agent.ResolvedBaseUrl}: {ex.Message}", false);
        }
    }

    /// <summary>Whether an error body is the endpoint saying this model cannot take tools.</summary>
    private static bool ToolsRejected(string body) =>
        body.Contains("support tools", StringComparison.OrdinalIgnoreCase)
        || body.Contains("does not support tool", StringComparison.OrdinalIgnoreCase)
        || body.Contains("tools are not supported", StringComparison.OrdinalIgnoreCase)
        || body.Contains("tool use is not supported", StringComparison.OrdinalIgnoreCase);

    private string BuildToolPayload(List<object> messages, bool withTools = true)
    {
        // Each tool is offered only when its permission is on, so an agent that may draw but not
        // run commands is handed the image tool alone. A function with no properties is not valid,
        // so the list is null rather than empty when nothing is offered.
        List<object>? tools = null;

        if (withTools)
        {
            tools = [];

            if (_tools is { Enabled: true })
            {
                tools.Add(Function(AgentTransports.Command.Name, AgentTransports.Command.Description, new()
                {
                    [AgentTransports.Command.Parameter] = Property("string", AgentTransports.Command.ParameterDescription),
                    [AgentTransports.Command.Elevated] = Property("boolean", AgentTransports.Command.ElevatedDescription),
                }, AgentTransports.Command.Parameter));

                tools.Add(Function(AgentTransports.Share.Name, AgentTransports.Share.Description, new()
                {
                    [AgentTransports.Share.Parameter] = Property("string", AgentTransports.Share.ParameterDescription),
                    [AgentTransports.Share.Note] = Property("string", AgentTransports.Share.NoteDescription),
                }, AgentTransports.Share.Parameter));

                // The file tools ride the same permission as the command tool.
                foreach (var spec in AgentTransports.Files.All)
                {
                    var props = spec.Parameters.ToDictionary(p => p.Name, p => Property(p.Type, p.Description));
                    var required = spec.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray();
                    tools.Add(Function(spec.Name, spec.Description, props, required));
                }
            }

            if (_tools is { ImagesEnabled: true })
            {
                tools.Add(Function(AgentTransports.Image.Name, AgentTransports.Image.Description, new()
                {
                    [AgentTransports.Image.Parameter] = Property("string", AgentTransports.Image.ParameterDescription),
                    [AgentTransports.Image.Negative] = Property("string", AgentTransports.Image.NegativeDescription),
                }, AgentTransports.Image.Parameter));
            }

            if (_tools is { AgentsEnabled: true })
            {
                tools.Add(Function(AgentTransports.Ask.Name, AgentTransports.Ask.Description, new()
                {
                    [AgentTransports.Ask.Parameter] = Property("string", AgentTransports.Ask.ParameterDescription),
                    [AgentTransports.Ask.Request] = Property("string", AgentTransports.Ask.RequestDescription),
                }, AgentTransports.Ask.Parameter));
            }

            if (_tools is { TasksEnabled: true })
            {
                tools.Add(Function(AgentTransports.Tasks.Name, AgentTransports.Tasks.Description, new()
                {
                    [AgentTransports.Tasks.Op] = Property("string", AgentTransports.Tasks.OpDescription),
                    [AgentTransports.Tasks.List] = Property("string", AgentTransports.Tasks.ListDescription),
                    [AgentTransports.Tasks.Id] = Property("string", AgentTransports.Tasks.IdDescription),
                    [AgentTransports.Tasks.TaskName] = Property("string", AgentTransports.Tasks.TaskNameDescription),
                    [AgentTransports.Tasks.Desc] = Property("string", AgentTransports.Tasks.DescDescription),
                    [AgentTransports.Tasks.State] = Property("string", AgentTransports.Tasks.StateDescription),
                    [AgentTransports.Tasks.Note] = Property("string", AgentTransports.Tasks.NoteDescription),
                }, AgentTransports.Tasks.Op));
            }

            if (tools.Count == 0)
            {
                tools = null;
            }
        }

        return JsonSerializer.Serialize(new
        {
            model = _agent.Model,
            max_tokens = _agent.MaxTokens,
            messages,
            tools,
        });
    }

    private static object Property(string type, string description) => new { type, description };

    private static object Function(string name, string description, Dictionary<string, object> properties, string required) =>
        Function(name, description, properties, new[] { required });

    private static object Function(string name, string description, Dictionary<string, object> properties, string[] required) => new
    {
        type = "function",
        function = new
        {
            name,
            description,
            parameters = new
            {
                type = "object",
                properties,
                required,
            },
        },
    };

    private async IAsyncEnumerable<AgentEvent> StreamAsync(
        IReadOnlyList<AgentMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Opening and reading both sit in helpers: C# forbids yielding from inside a catch, and
        // every failure here has to reach the caller as an event rather than as an exception.
        var opened = await OpenAsync(conversation, cancellationToken).ConfigureAwait(false);

        if (opened.Error is not null)
        {
            yield return AgentEvent.Failure(opened.Error);
            yield break;
        }

        if (opened.Reader is null)
        {
            yield break;
        }

        var sawText = false;
        var failure = default(string);

        try
        {
            while (true)
            {
                var (line, error) = await ReadLineAsync(opened.Reader, cancellationToken).ConfigureAwait(false);

                if (error is not null)
                {
                    failure = error;
                    break;
                }

                if (line is null)
                {
                    break;
                }

                // Server-sent events: payload lines are "data: ...", anything else (comments the
                // server sends to hold the connection open, blank separators) is skipped.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                if (data.Length == 0)
                {
                    continue;
                }

                // Usage rides on a chunk of its own at the very end, and only when it was asked
                // for; an endpoint that ignores the request simply never sends one.
                if (ReadUsage(data) is var (input, output) && (input > 0 || output > 0))
                {
                    yield return AgentEvent.Spent(input, output);
                }

                if (ReadReasoning(data) is { Length: > 0 } thought)
                {
                    yield return new AgentEvent(AgentEventKind.Thinking, thought);
                }

                if (ReadDelta(data) is { Length: > 0 } text)
                {
                    if (!sawText)
                    {
                        sawText = true;
                        yield return AgentEvent.Doing(AgentPhase.Writing);
                    }

                    yield return AgentEvent.OfText(text);
                }
            }
        }
        finally
        {
            opened.Dispose();
        }

        yield return failure is not null
            ? AgentEvent.Failure(failure)
            : new AgentEvent(
                AgentEventKind.Completed,
                sawText ? string.Empty : "The endpoint returned no text for this turn.");
    }

    /// <summary>The pieces of an open streaming response, or the reason there is none.</summary>
    private sealed record Opened(
        HttpResponseMessage? Response, Stream? Body, StreamReader? Reader, string? Error)
    {
        public void Dispose()
        {
            Reader?.Dispose();
            Body?.Dispose();
            Response?.Dispose();
        }
    }

    private async Task<Opened> OpenAsync(
        IReadOnlyList<AgentMessage> conversation, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
            {
                Content = new StringContent(
                    BuildPayload(conversation, stream: true), Encoding.UTF8, "application/json"),
            };

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var message = Describe(response, detail);
                response.Dispose();
                return new Opened(null, null, null, message);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new Opened(response, body, new StreamReader(body, Encoding.UTF8), null);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();

            // Cancelling is the user's doing: no stream, and nothing to report.
            return new Opened(null, null, null, null);
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            return new Opened(null, null, null, $"Could not reach {_agent.ResolvedBaseUrl}: {ex.Message}");
        }
    }

    private static async Task<(string? Line, string? Error)> ReadLineAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (IOException ex)
        {
            return (null, $"The stream ended early: {ex.Message}");
        }
    }

    public async Task<string?> TestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The model list is the cheapest endpoint every implementation of this shape offers,
            // and reaching it proves the base URL and the key together.
            using var response = await _http.GetAsync(Endpoint("models"), cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Describe(response, detail);
        }
        catch (OperationCanceledException)
        {
            return "The connection test was cancelled.";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach {_agent.ResolvedBaseUrl}: {ex.Message}";
        }
    }

    /// <summary>
    /// The full URL for one API path, adding the version segment only when the configured base
    /// does not already carry it — proxies are commonly published with it baked in.
    /// </summary>
    private string Endpoint(string path)
    {
        var root = _agent.ResolvedBaseUrl;

        // A base that already ends where the paths hang off it — the standard "/v1", or Google's
        // "/v1beta/openai" for Gemini — takes the path directly; anything else gets the version
        // segment added, the way a bare provider root is published.
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
               || root.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)
            ? $"{root}/{path}"
            : $"{root}/v1/{path}";
    }

    private string BuildPayload(IReadOnlyList<AgentMessage> conversation, bool stream)
    {
        var messages = new List<object>();

        // This format carries standing instructions as a leading message rather than a field.
        if (!string.IsNullOrWhiteSpace(_agent.Instructions))
        {
            messages.Add(new { role = "system", content = _agent.Instructions });
        }

        foreach (var message in conversation)
        {
            messages.Add(ToMessage(message));
        }

        return JsonSerializer.Serialize(new
        {
            model = _agent.Model,
            max_tokens = _agent.MaxTokens,
            stream,

            // Asks for the token counts at the end of a stream. Endpoints that do not know the
            // option ignore it, which costs nothing — the counter simply falls back to its own
            // estimate.
            stream_options = stream ? new { include_usage = true } : null,
            messages,
        });
    }

    /// <summary>
    /// One turn in the shape this format wants: a plain string when there is only text, a list of
    /// parts when pictures came with it.
    /// </summary>
    /// <remarks>
    /// Pictures ride as <c>data:</c> URLs rather than links: the files are on the user's machine,
    /// so there is nothing for the endpoint to fetch, and uploading them somewhere first would
    /// put the user's screenshots on a third host to save a few kilobytes on the wire.
    /// </remarks>
    private static object ToMessage(AgentMessage message)
    {
        var role = message.Role == AgentRole.User ? "user" : "assistant";

        if (message.Images is not { Count: > 0 } images)
        {
            return new { role, content = message.Text };
        }

        var parts = new List<object>();

        foreach (var image in images)
        {
            parts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{image.MediaType};base64,{image.Base64}" },
            });
        }

        if (message.Text.Length > 0)
        {
            parts.Add(new { type = "text", text = message.Text });
        }

        return new { role, content = parts };
    }

    /// <summary>What a chunk says the turn has cost, or zeroes when it says nothing.</summary>
    private static (long Input, long Output) ReadUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return (0, 0);
            }

            return (Count(usage, "prompt_tokens"), Count(usage, "completion_tokens"));
        }
        catch (JsonException)
        {
            return (0, 0);
        }

        static long Count(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : 0;
    }

    /// <summary>Pulls the text fragment out of one streamed chunk, if it carries one.</summary>
    private static string? ReadDelta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];

            // "delta" while streaming; "message" is accepted too, since some implementations
            // send a whole message as the final chunk.
            if (!choice.TryGetProperty("delta", out var part)
                && !choice.TryGetProperty("message", out part))
            {
                return null;
            }

            return part.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The model's reasoning from one streamed chunk, for endpoints that send it.
    /// </summary>
    /// <remarks>
    /// Two spellings are in the wild for the same thing, and neither is in the original
    /// specification — so both are read, and an endpoint that sends neither simply has no
    /// reasoning to show.
    /// </remarks>
    private static string? ReadReasoning(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];

            if (!choice.TryGetProperty("delta", out var part)
                && !choice.TryGetProperty("message", out part))
            {
                return null;
            }

            foreach (var name in (string[])["reasoning_content", "reasoning"])
            {
                if (part.TryGetProperty(name, out var thought) && thought.ValueKind == JsonValueKind.String)
                {
                    return thought.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            // A chunk that does not parse is not worth failing the turn over.
            return null;
        }
    }

    private static string Describe(HttpResponseMessage response, string detail)
    {
        var reason = (int)response.StatusCode switch
        {
            401 or 403 => "The API key was rejected",
            404 => "No such endpoint or model",
            429 => "Rate limited",
            >= 500 => "The endpoint reported a server error",
            _ => "The request was refused",
        };

        detail = detail.Trim();
        if (detail.Length > 300)
        {
            detail = detail[..300] + "…";
        }

        return detail.Length > 0
            ? $"{reason} ({(int)response.StatusCode}): {detail}"
            : $"{reason} ({(int)response.StatusCode}).";
    }

    public void Dispose() => _http.Dispose();
}
