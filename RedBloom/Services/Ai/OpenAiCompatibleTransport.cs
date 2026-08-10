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
    private const int MaxToolSteps = 12;

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

    private bool ToolsEnabled => _tools is { Enabled: true };

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

        if (!string.IsNullOrWhiteSpace(_agent.SystemPrompt))
        {
            messages.Add(new { role = "system", content = _agent.SystemPrompt });
        }

        foreach (var message in conversation)
        {
            messages.Add(ToMessage(message));
        }

        for (var step = 0; step < MaxToolSteps; step++)
        {
            var (json, error) = await PostAsync(messages, cancellationToken).ConfigureAwait(false);

            if (error is not null || json is null)
            {
                yield return AgentEvent.Failure(error ?? "The endpoint returned nothing.");
                yield break;
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

            foreach (var (id, command) in calls)
            {
                yield return new AgentEvent(AgentEventKind.ToolCall, command);

                var approved = await _tools!.ApproveAsync(command, cancellationToken).ConfigureAwait(false);
                string output;

                if (approved)
                {
                    output = await _tools.RunAsync(command, cancellationToken).ConfigureAwait(false);
                    yield return new AgentEvent(AgentEventKind.ToolResult, output);
                }
                else
                {
                    yield return new AgentEvent(AgentEventKind.ToolRefused, command);
                    output = "The user declined to run this command.";
                }

                messages.Add(new { role = "tool", tool_call_id = id, content = output });
            }
        }

        yield return AgentEvent.Failure(
            $"The agent was still running commands after {MaxToolSteps} rounds; the turn was stopped.");
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

    private static List<(string Id, string Command)> ReadToolCalls(JsonElement message)
    {
        var calls = new List<(string, string)>();

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

            // Arguments arrive as a JSON string holding JSON, so they are parsed twice.
            try
            {
                using var parsed = JsonDocument.Parse(arguments.GetString() ?? "{}");

                if (parsed.RootElement.TryGetProperty(AgentTransports.Command.Parameter, out var command)
                    && command.ValueKind == JsonValueKind.String)
                {
                    calls.Add((id.GetString() ?? string.Empty, command.GetString() ?? string.Empty));
                }
            }
            catch (JsonException)
            {
                // A call whose arguments do not parse cannot be run; skipping it leaves the
                // model to notice the missing result and try again.
            }
        }

        return calls;
    }

    private async Task<(string? Json, string? Error)> PostAsync(
        List<object> messages, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
            {
                Content = new StringContent(
                    BuildToolPayload(messages), Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? (body, null)
                : (null, Describe(response, body));
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not reach {_agent.ResolvedBaseUrl}: {ex.Message}");
        }
    }

    private string BuildToolPayload(List<object> messages) => JsonSerializer.Serialize(new
    {
        model = _agent.Model,
        max_tokens = _agent.MaxTokens,
        messages,
        tools = new[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = AgentTransports.Command.Name,
                    description = AgentTransports.Command.Description,
                    parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            [AgentTransports.Command.Parameter] = new
                            {
                                type = "string",
                                description = AgentTransports.Command.ParameterDescription,
                            },
                        },
                        required = new[] { AgentTransports.Command.Parameter },
                    },
                },
            },
        },
    });

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

                if (data.Length > 0 && ReadDelta(data) is { Length: > 0 } text)
                {
                    sawText = true;
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
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{root}/{path}"
            : $"{root}/v1/{path}";
    }

    private string BuildPayload(IReadOnlyList<AgentMessage> conversation, bool stream)
    {
        var messages = new List<object>();

        // This format carries standing instructions as a leading message rather than a field.
        if (!string.IsNullOrWhiteSpace(_agent.SystemPrompt))
        {
            messages.Add(new { role = "system", content = _agent.SystemPrompt });
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
