using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Talks to Anthropic's Messages API through the official SDK.
/// </summary>
/// <remarks>
/// Thinking is adaptive when it is on — the model decides per turn how much a question is worth,
/// so there is no token budget to set here (the old fixed-budget form is rejected outright by
/// current models). Depth is steered with effort instead, and the raw reasoning is never
/// returned, so a thinking turn shows as a pause rather than as text.
/// </remarks>
public sealed class AnthropicTransport : IAgentTransport
{
    /// <summary>How many command rounds one turn may take before it is called a loop.</summary>
    private const int MaxToolSteps = 12;

    private readonly AiAgent _agent;
    private readonly IAgentToolHost? _tools;
    private readonly AnthropicClient _client;

    public AnthropicTransport(AiAgent agent, IAgentToolHost? tools = null)
    {
        _agent = agent;
        _tools = tools;
        _client = new AnthropicClient
        {
            ApiKey = agent.ApiKey ?? string.Empty,
            BaseUrl = agent.ResolvedBaseUrl,
        };
    }

    private bool ToolsEnabled => _tools is { Enabled: true };

    public IAsyncEnumerable<AgentEvent> SendAsync(
        IReadOnlyList<AgentMessage> conversation,
        CancellationToken cancellationToken = default) =>
        ToolsEnabled
            ? SendWithToolsAsync(conversation, cancellationToken)
            : StreamAsync(conversation, cancellationToken);

    private async IAsyncEnumerable<AgentEvent> StreamAsync(
        IReadOnlyList<AgentMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_agent.ApiKey))
        {
            yield return AgentEvent.Failure("No API key is set for this agent.");
            yield break;
        }

        if (_agent.Thinking)
        {
            yield return new AgentEvent(AgentEventKind.Thinking, string.Empty);
        }

        var (stream, openError) = OpenStream(conversation, cancellationToken);
        if (openError is not null || stream is null)
        {
            yield return AgentEvent.Failure(openError ?? "The request could not be started.");
            yield break;
        }

        var sawText = false;
        var failure = default(string);

        // The reads sit in a helper rather than a try/catch here: C# forbids yielding from
        // inside a catch, and every failure has to reach the caller as an event.
        try
        {
            while (true)
            {
                var step = await NextAsync(stream).ConfigureAwait(false);

                if (step.Error is not null)
                {
                    failure = step.Error;
                    break;
                }

                if (!step.Moved)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(step.Text))
                {
                    sawText = true;
                    yield return AgentEvent.OfText(step.Text);
                }
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        yield return failure is not null
            ? AgentEvent.Failure(failure)
            : new AgentEvent(
                AgentEventKind.Completed,
                sawText ? string.Empty : "The model returned no text for this turn.");
    }

    public async Task<string?> TestAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_agent.ApiKey))
        {
            return "No API key is set.";
        }

        try
        {
            // Asking the models endpoint about this exact id checks the endpoint, the key and
            // the model name in one call, and unlike a probe message it generates no tokens.
            await _client.Models.Retrieve(_agent.Model).WaitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Describe(ex);
        }
    }

    /// <summary>
    /// The turn as a request-and-run loop, used when the agent may execute commands.
    /// </summary>
    /// <remarks>
    /// Each round is still a streamed request — see <see cref="CollectAsync"/> for why — but the
    /// text of a round only reaches the screen once that round is complete, because the loop has
    /// to know whether a command was asked for before it can go on. Thinking is off for these
    /// turns: the API wants thinking blocks echoed back beside a tool call, and a block cannot be
    /// put back together from a stream, so it is not requested rather than sent back damaged.
    /// </remarks>
    private async IAsyncEnumerable<AgentEvent> SendWithToolsAsync(
        IReadOnlyList<AgentMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_agent.ApiKey))
        {
            yield return AgentEvent.Failure("No API key is set for this agent.");
            yield break;
        }

        var messages = new List<MessageParam>(conversation.Select(ToParam));

        for (var step = 0; step < MaxToolSteps; step++)
        {
            var turn = await CollectAsync(messages, cancellationToken).ConfigureAwait(false);

            if (turn.Error is not null)
            {
                yield return AgentEvent.Failure(turn.Error);
                yield break;
            }

            if (turn.Text.Length > 0)
            {
                yield return AgentEvent.OfText(turn.Text);
            }

            if (turn.Calls.Count == 0)
            {
                yield return new AgentEvent(AgentEventKind.Completed, string.Empty);
                yield break;
            }

            var assistant = new List<ContentBlockParam>();

            if (turn.Text.Length > 0)
            {
                assistant.Add(new TextBlockParam { Text = turn.Text });
            }

            foreach (var call in turn.Calls)
            {
                assistant.Add(new ToolUseBlockParam
                {
                    ID = call.Id,
                    Name = call.Name,
                    Input = ParseArguments(call.Arguments),
                });
            }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistant });

            var results = new List<ContentBlockParam>();

            foreach (var (id, command) in turn.Calls.Select(c => (c.Id, ReadCommand(c.Arguments))))
            {
                yield return new AgentEvent(AgentEventKind.ToolCall, command);

                var approved = await _tools!.ApproveAsync(command, cancellationToken).ConfigureAwait(false);

                if (!approved)
                {
                    yield return new AgentEvent(AgentEventKind.ToolRefused, command);

                    // Refusal goes back as a result rather than ending the turn, so the model
                    // can say what it would have done or try something the user will allow.
                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = id,
                        Content = "The user declined to run this command.",
                        IsError = true,
                    });

                    continue;
                }

                var output = await _tools.RunAsync(command, cancellationToken).ConfigureAwait(false);
                yield return new AgentEvent(AgentEventKind.ToolResult, output);

                results.Add(new ToolResultBlockParam { ToolUseID = id, Content = output });
            }

            messages.Add(new MessageParam { Role = Role.User, Content = results });
        }

        yield return AgentEvent.Failure(
            $"The agent was still running commands after {MaxToolSteps} rounds; the turn was stopped.");
    }

    /// <summary>One assistant turn, gathered from the stream.</summary>
    private sealed record Collected(string Text, List<ToolCall> Calls, string? Error);

    private sealed record ToolCall(string Id, string Name, string Arguments);

    /// <summary>
    /// Runs one request and reassembles the reply from its stream.
    /// </summary>
    /// <remarks>
    /// Streamed even though the loop needs the whole turn before it can act: a gateway is free
    /// to answer a plain request in a shape of its own, and at least one in use here returns
    /// OpenAI-style JSON to a non-streaming call while streaming a correct Anthropic sequence
    /// from the same path. Asking for the stream is what makes the answer parseable.
    /// A tool call's arguments arrive as JSON in fragments, so they are stitched back together
    /// here before being read.
    /// </remarks>
    private async Task<Collected> CollectAsync(
        List<MessageParam> messages, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();

        string? openId = null;
        string? openName = null;
        var arguments = new StringBuilder();

        try
        {
            await foreach (var item in _client.Messages
                               .CreateStreaming(BuildRequest(messages))
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (item.TryPickContentBlockStart(out var start))
                {
                    if (start.ContentBlock.TryPickToolUse(out var tool))
                    {
                        openId = tool.ID;
                        openName = tool.Name;
                        arguments.Clear();
                    }
                }
                else if (item.TryPickContentBlockDelta(out var delta))
                {
                    if (delta.Delta.TryPickText(out var chunk))
                    {
                        text.Append(chunk.Text);
                    }
                    else if (delta.Delta.TryPickInputJson(out var partial))
                    {
                        arguments.Append(partial.PartialJson);
                    }
                }
                else if (item.TryPickContentBlockStop(out _) && openId is not null)
                {
                    calls.Add(new ToolCall(openId, openName ?? string.Empty, arguments.ToString()));
                    openId = null;
                    openName = null;
                    arguments.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new Collected(string.Empty, [], null);
        }
        catch (Exception ex)
        {
            return new Collected(string.Empty, [], Describe(ex));
        }

        return new Collected(text.ToString(), calls, null);
    }

    /// <summary>The tool call's arguments as the SDK wants them when the turn is echoed back.</summary>
    private static Dictionary<string, JsonElement> ParseArguments(string json)
    {
        var input = new Dictionary<string, JsonElement>();

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    input[property.Name] = property.Value.Clone();
                }
            }
        }
        catch (JsonException)
        {
            // Arguments that do not parse leave an empty object; the command below comes out
            // blank and the model is told the call did nothing.
        }

        return input;
    }

    /// <summary>Pulls the command out of a tool call's arguments.</summary>
    private static string ReadCommand(string json) =>
        ParseArguments(json).TryGetValue(AgentTransports.Command.Parameter, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>One read from the stream: whether it moved, what it carried, why it stopped.</summary>
    private readonly record struct Step(bool Moved, string? Text, string? Error);

    private (IAsyncEnumerator<RawMessageStreamEvent>? Stream, string? Error) OpenStream(
        IReadOnlyList<AgentMessage> conversation, CancellationToken cancellationToken)
    {
        try
        {
            var stream = _client.Messages
                .CreateStreaming(BuildRequest(conversation))
                .GetAsyncEnumerator(cancellationToken);
            return (stream, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, Describe(ex));
        }
    }

    private static async Task<Step> NextAsync(IAsyncEnumerator<RawMessageStreamEvent> stream)
    {
        try
        {
            if (!await stream.MoveNextAsync().ConfigureAwait(false))
            {
                return new Step(false, null, null);
            }

            return stream.Current.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text)
                ? new Step(true, text.Text, null)
                : new Step(true, null, null);
        }
        catch (OperationCanceledException)
        {
            // A cancelled turn is the user's doing; it ends the stream without an error line.
            return new Step(false, null, null);
        }
        catch (Exception ex)
        {
            return new Step(false, null, Describe(ex));
        }
    }

    private MessageCreateParams BuildRequest(IReadOnlyList<AgentMessage> conversation) =>
        BuildRequest([.. conversation.Select(ToParam)]);

    /// <summary>
    /// One turn in the shape this API wants: a plain string when there is only text, a list of
    /// blocks when pictures came with it.
    /// </summary>
    /// <remarks>
    /// Pictures go before the text because the sentence about them almost always follows them —
    /// "what is wrong in this screenshot" reads as a question about the block above it.
    /// </remarks>
    private static MessageParam ToParam(AgentMessage message)
    {
        var role = message.Role == AgentRole.User ? Role.User : Role.Assistant;

        if (message.Images is not { Count: > 0 } images)
        {
            return new MessageParam { Role = role, Content = message.Text };
        }

        var content = new List<ContentBlockParam>();

        foreach (var image in images)
        {
            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = image.Base64,
                    MediaType = MediaFor(image.MediaType),
                },
            });
        }

        if (message.Text.Length > 0)
        {
            content.Add(new TextBlockParam { Text = message.Text });
        }

        return new MessageParam { Role = role, Content = content };
    }

    private static MediaType MediaFor(string media) => media switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImagePng,
    };

    private MessageCreateParams BuildRequest(List<MessageParam> messages)
    {
        var thinking = _agent.Thinking && !ToolsEnabled;

        return new MessageCreateParams
        {
            Tools = ToolsEnabled
                ?
                [
                    new Tool
                    {
                        Name = AgentTransports.Command.Name,
                        Description = AgentTransports.Command.Description,
                        InputSchema = new()
                        {
                            Properties = new Dictionary<string, System.Text.Json.JsonElement>
                            {
                                [AgentTransports.Command.Parameter] =
                                    System.Text.Json.JsonSerializer.SerializeToElement(new
                                    {
                                        type = "string",
                                        description = AgentTransports.Command.ParameterDescription,
                                    }),
                            },
                            Required = [AgentTransports.Command.Parameter],
                        },
                    },
                ]
                : null,
            Model = _agent.Model,
            MaxTokens = _agent.MaxTokens,
            // Adaptive, never a fixed budget — the budget form is rejected by current models.
            // Disabled has to be sent explicitly: omitting the field leaves thinking on.
            Thinking = thinking
                ? new ThinkingConfigAdaptive()
                : (ThinkingConfigParam)new ThinkingConfigDisabled(),
            OutputConfig = new OutputConfig { Effort = EffortFor(_agent.Effort, thinking) },
            // Cast so the empty branch is a genuine null of the union type. Without it both
            // branches type as the list, and converting a null list into the union throws
            // before the request is ever sent.
            System = string.IsNullOrWhiteSpace(_agent.SystemPrompt)
                ? (MessageCreateParamsSystem?)null
                : new List<TextBlockParam> { new() { Text = _agent.SystemPrompt } },
            Messages = messages,
        };
    }

    /// <summary>
    /// The effort level to ask for, lowered when it would contradict the thinking setting.
    /// </summary>
    /// <remarks>
    /// Turning thinking off is only accepted up to <c>high</c>; pairing it with a higher level is
    /// a 400. Comparison is by name rather than by enum member so that a level this build of the
    /// SDK has no constant for still gets caught.
    /// </remarks>
    private static Effort EffortFor(string? configured, bool thinking)
    {
        var name = configured?.Trim() ?? string.Empty;

        if (!thinking && (name.Equals("max", StringComparison.OrdinalIgnoreCase)
                          || name.Equals("xhigh", StringComparison.OrdinalIgnoreCase)))
        {
            return Effort.High;
        }

        return Enum.TryParse<Effort>(name, ignoreCase: true, out var parsed) ? parsed : Effort.High;
    }

    private static string Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => "The API key was rejected (401).",
        AnthropicNotFoundException =>
            $"No such model or endpoint — check the model id and the base URL. ({ex.Message})",
        AnthropicRateLimitException => "Rate limited (429). Try again shortly.",
        _ => ex.Message,
    };

    public void Dispose()
    {
        // The SDK client owns no unmanaged handles that need releasing here; the method exists
        // because the OpenAI-compatible transport's HttpClient does.
    }
}
