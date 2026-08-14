using System.Collections.Concurrent;
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
    /// <summary>
    /// How many command rounds one turn may take before it is made to stop and report.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Real work — look, build, read the errors, fix, build again — spends
    /// rounds quickly, and a limit tight enough to catch a loop early is also tight enough to cut
    /// off the tasks worth asking for. Running out is no longer a failure either, so the cost of
    /// setting it high is some wasted commands rather than a lost answer.
    /// </remarks>
    internal const int MaxToolSteps = 40;

    /// <summary>What the model is told when it runs out of rounds.</summary>
    internal const string OutOfRounds =
        "You have used all the commands allowed for this turn, and no more will run. Do not ask "
        + "to run anything else. Tell the user what you found, what you changed, and what is "
        + "left to do, so they can decide whether to have you carry on.";

    /// <summary>
    /// Endpoint-and-model pairs that have rejected the advanced fields, so later requests skip them.
    /// </summary>
    /// <remarks>
    /// Remembered app-wide, not per transport: a room builds a fresh transport every turn, and
    /// learning the endpoint's limit once should hold for all of them rather than costing a failed
    /// request each time. Keyed by endpoint and model together, because the same relay may accept
    /// the fields for one backing model and reject them for another.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, byte> DroppedAdvanced = new();

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

    private bool ToolsEnabled =>
        _tools is not null && (_tools.Enabled || _tools.ImagesEnabled || _tools.AgentsEnabled || _tools.TasksEnabled);

    /// <summary>The key this endpoint-and-model pair is remembered under.</summary>
    private string AdvancedKey => _agent.ResolvedBaseUrl + "\n" + _agent.Model;

    /// <summary>Whether the proprietary thinking/effort fields go on this request.</summary>
    private bool IncludeAdvanced => !DroppedAdvanced.ContainsKey(AdvancedKey);

    public IAsyncEnumerable<AgentEvent> SendAsync(
        IReadOnlyList<AgentMessage> conversation,
        CancellationToken cancellationToken = default) =>
        WithRetries(
            () => ToolsEnabled
                ? SendWithToolsAsync(conversation, cancellationToken)
                : StreamAsync(conversation, cancellationToken),
            cancellationToken);

    /// <summary>How long to wait before each reconnect after a transient upstream error.</summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(10),
    ];

    /// <summary>
    /// Runs a turn and recovers from the two failures worth recovering from: an endpoint that
    /// rejects the proprietary fields, and a passing upstream fault.
    /// </summary>
    /// <remarks>
    /// A retry is only taken when nothing was produced first — both faults land before any text, so
    /// a turn that had begun answering when it failed is a real failure and is passed through. A
    /// field rejection drops the fields for this endpoint and retries at once; a transient upstream
    /// error (a 502/503, an overload, a timeout) reconnects after 1, 3, 6 and 10 seconds before
    /// giving up. Cancellation is never retried.
    /// </remarks>
    private async IAsyncEnumerable<AgentEvent> WithRetries(
        Func<IAsyncEnumerable<AgentEvent>> attempt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reconnects = 0;

        while (true)
        {
            var sentAdvanced = IncludeAdvanced;
            var progressed = false;
            string? failure = null;

            await foreach (var item in attempt().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (item.Kind == AgentEventKind.Failed)
                {
                    failure = item.Text;
                    break;
                }

                if (item.Kind is AgentEventKind.Text or AgentEventKind.ToolCall or AgentEventKind.Image
                    || (item.Kind == AgentEventKind.Thinking && item.Text.Length > 0))
                {
                    progressed = true;
                }

                yield return item;
            }

            // Clean end, or a fault mid-answer that is not ours to retry.
            if (failure is null)
            {
                yield break;
            }

            if (cancellationToken.IsCancellationRequested || progressed)
            {
                yield return AgentEvent.Failure(failure);
                yield break;
            }

            // The endpoint would not take the advanced fields: drop them here and retry at once.
            if (sentAdvanced && LooksLikeFieldRejection(failure))
            {
                DroppedAdvanced[AdvancedKey] = 1;
                continue;
            }

            // A passing upstream error: wait out the backoff and reconnect, up to the last delay.
            if (reconnects < Backoff.Length && LooksTransient(failure))
            {
                yield return AgentEvent.Doing(AgentPhase.Reconnecting);

                try
                {
                    await Task.Delay(Backoff[reconnects], cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                reconnects++;
                continue;
            }

            yield return AgentEvent.Failure(failure);
            yield break;
        }
    }

    /// <summary>Whether a failure reads like the endpoint refusing the request over its fields.</summary>
    private static bool LooksLikeFieldRejection(string message) => Mentions(message,
        "output_config", "effort", "thinking", "invalid_request", "unsupported",
        "not support", "unexpected", "unknown field", "unrecognized", "reject", "400");

    /// <summary>
    /// Whether a failure is a passing upstream fault worth reconnecting over rather than a settled
    /// refusal. Auth and bad-request faults are left out: retrying those only wastes the wait.
    /// </summary>
    private static bool LooksTransient(string message) => Mentions(message,
        "502", "503", "500", "504", "429", "overloaded", "rate limit", "timeout", "timed out",
        "temporarily", "unavailable", "returned an error", "try again", "gateway", "connection reset");

    private static bool Mentions(string message, params string[] marks)
    {
        foreach (var mark in marks)
        {
            if (message.Contains(mark, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
        long spentIn = 0;
        long spentOut = 0;

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

                // Assigned, not added: each of these events carries the running total for the
                // message, so summing them would count the same tokens several times over.
                if (step.Input > 0 || step.Output > 0)
                {
                    spentIn = step.Input > 0 ? step.Input : spentIn;
                    spentOut = step.Output > 0 ? step.Output : spentOut;
                    yield return AgentEvent.Spent(spentIn, spentOut);
                }

                if (!string.IsNullOrEmpty(step.Thinking))
                {
                    yield return new AgentEvent(AgentEventKind.Thinking, step.Thinking);
                }

                if (!string.IsNullOrEmpty(step.Text))
                {
                    if (!sawText)
                    {
                        sawText = true;
                        yield return AgentEvent.Doing(AgentPhase.Writing);
                    }

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

        // Both totals accumulate across rounds: one turn with commands in it is several requests,
        // and what the user is spending is their sum, not the last one.
        long spentIn = 0;
        long spentOut = 0;

        for (var step = 0; step < MaxToolSteps; step++)
        {
            yield return AgentEvent.Doing(step == 0 ? AgentPhase.Thinking : AgentPhase.Deciding);

            var turn = await CollectAsync(messages, cancellationToken).ConfigureAwait(false);

            if (turn.Error is not null)
            {
                yield return AgentEvent.Failure(turn.Error);
                yield break;
            }

            spentIn += turn.Input;
            spentOut += turn.Output;
            yield return AgentEvent.Spent(spentIn, spentOut);

            if (turn.Text.Length > 0)
            {
                yield return AgentEvent.Doing(AgentPhase.Writing);
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

            foreach (var call in turn.Calls)
            {
                var id = call.Id;

                // Handing a file over needs no approval: it opens nothing and runs nothing, it
                // only shows the user something already on their own disk.
                if (call.Name == AgentTransports.Share.Name)
                {
                    var (shared, note) = ReadShare(call.Arguments);

                    yield return AgentEvent.Doing(AgentPhase.Sharing);

                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = id,
                        Content = await _tools!.ShareAsync(shared, note, cancellationToken).ConfigureAwait(false),
                    });

                    continue;
                }

                // Drawing a picture also needs no approval: it runs a diffusion model and shows
                // the result, the same standing as sharing a file.
                if (call.Name == AgentTransports.Image.Name)
                {
                    var (prompt, negative) = ReadImage(call.Arguments);

                    yield return AgentEvent.Doing(AgentPhase.Drawing);

                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = id,
                        Content = await _tools!.GenerateImageAsync(prompt, negative, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                // The file tools carry their own approval inside the host (a write or edit is put
                // to the user, a read or listing is not), so they are dispatched straight through
                // with the whole arguments object.
                if (AgentTransports.Files.Names.Contains(call.Name))
                {
                    yield return AgentEvent.Doing(
                        call.Name is AgentTransports.Files.Write or AgentTransports.Files.Edit
                            ? AgentPhase.WritingFile
                            : AgentPhase.ReadingFile);

                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = id,
                        Content = await _tools!.FileToolAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false),
                    });

                    continue;
                }

                // Keeping the task list changes only a list on the user's own screen, so it takes
                // no approval either. The whole arguments object is handed over as it arrived.
                if (call.Name == AgentTransports.Tasks.Name)
                {
                    yield return AgentEvent.Doing(AgentPhase.Tasks);

                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = id,
                        Content = await _tools!.ManageTasksAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
                    });

                    continue;
                }

                // Asking another agent runs it and returns what it produced; no approval, the same
                // as the other tools that make rather than change.
                if (call.Name == AgentTransports.Ask.Name)
                {
                    var (who, request) = ReadAsk(call.Arguments);

                    yield return AgentEvent.Doing(AgentPhase.Asking);

                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = id,
                        Content = await _tools!.AskAgentAsync(who, request, cancellationToken)
                            .ConfigureAwait(false),
                    });

                    continue;
                }

                var command = ReadCommand(call.Arguments);
                var elevated = ReadElevated(call.Arguments);

                yield return new AgentEvent(AgentEventKind.ToolCall, command);

                var approved = await _tools!.ApproveAsync(command, elevated, cancellationToken).ConfigureAwait(false);

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

                yield return AgentEvent.Doing(elevated ? AgentPhase.RunningElevated : AgentPhase.Running);

                var output = await _tools.RunAsync(command, elevated, cancellationToken).ConfigureAwait(false);

                yield return new AgentEvent(AgentEventKind.ToolResult, output);
                yield return AgentEvent.Doing(AgentPhase.ReadingOutput);

                results.Add(new ToolResultBlockParam { ToolUseID = id, Content = output });
            }

            messages.Add(new MessageParam { Role = Role.User, Content = results });
        }

        // Out of rounds. Rather than throwing the turn away — which loses everything that was
        // found on the way — the model is asked once more with no tools at all, so it has to
        // answer in words. The user then has a report and can say whether to carry on.
        messages.Add(new MessageParam { Role = Role.User, Content = OutOfRounds });

        yield return AgentEvent.Doing(AgentPhase.WrappingUp);

        var closing = await CollectAsync(messages, cancellationToken, withTools: false).ConfigureAwait(false);

        if (closing.Error is not null)
        {
            yield return AgentEvent.Failure(closing.Error);
            yield break;
        }

        yield return AgentEvent.Spent(spentIn + closing.Input, spentOut + closing.Output);

        if (closing.Text.Length > 0)
        {
            yield return AgentEvent.OfText(closing.Text);
        }

        yield return new AgentEvent(AgentEventKind.Completed, string.Empty);
    }

    /// <summary>One assistant turn, gathered from the stream.</summary>
    private sealed record Collected(
        string Text, List<ToolCall> Calls, string? Error, long Input = 0, long Output = 0);

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
        List<MessageParam> messages, CancellationToken cancellationToken, bool withTools = true)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();

        string? openId = null;
        string? openName = null;
        var arguments = new StringBuilder();
        long input = 0;
        long output = 0;

        try
        {
            await foreach (var item in _client.Messages
                               .CreateStreaming(BuildRequest(messages, withTools))
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
                else if (item.TryPickStart(out var opening))
                {
                    input = opening.Message.Usage.InputTokens;
                }
                else if (item.TryPickDelta(out var closing))
                {
                    output = closing.Usage.OutputTokens;
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

        return new Collected(text.ToString(), calls, null, input, output);
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

    /// <summary>Whether the call asked for administrator rights. Absent means no.</summary>
    private static bool ReadElevated(string json) =>
        ParseArguments(json).TryGetValue(AgentTransports.Command.Elevated, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static (string Path, string Note) ReadShare(string json)
    {
        var arguments = ParseArguments(json);

        return (Text(AgentTransports.Share.Parameter), Text(AgentTransports.Share.Note));

        string Text(string name) =>
            arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static (string Prompt, string Negative) ReadImage(string json)
    {
        var arguments = ParseArguments(json);

        return (Text(AgentTransports.Image.Parameter), Text(AgentTransports.Image.Negative));

        string Text(string name) =>
            arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static (string Agent, string Request) ReadAsk(string json)
    {
        var arguments = ParseArguments(json);

        return (Text(AgentTransports.Ask.Parameter), Text(AgentTransports.Ask.Request));

        string Text(string name) =>
            arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>One read from the stream: whether it moved, what it carried, why it stopped.</summary>
    private readonly record struct Step(
        bool Moved, string? Text, string? Error, long Input = 0, long Output = 0, string? Thinking = null);

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

            if (stream.Current.TryPickContentBlockDelta(out var delta))
            {
                if (delta.Delta.TryPickText(out var text))
                {
                    return new Step(true, text.Text, null);
                }

                // The reasoning, where the model is asked to do it aloud. Carried separately
                // from the answer so the page can fold it away.
                if (delta.Delta.TryPickThinking(out var thought))
                {
                    return new Step(true, null, null, Thinking: thought.Thinking);
                }
            }

            // What the turn cost, as the endpoint counts it. The prompt total lands at the start,
            // the written total at the end, so the two arrive on different events.
            if (stream.Current.TryPickStart(out var opening))
            {
                return new Step(true, null, null, Input: opening.Message.Usage.InputTokens);
            }

            if (stream.Current.TryPickDelta(out var closing))
            {
                return new Step(true, null, null, Output: closing.Usage.OutputTokens);
            }

            return new Step(true, null, null);
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

    private MessageCreateParams BuildRequest(List<MessageParam> messages, bool withTools = true)
    {
        var offerTools = ToolsEnabled && withTools;
        var thinking = _agent.Thinking && !offerTools;

        // Extended thinking and output_config/effort are Anthropic's newest, proprietary fields. The
        // official API takes them; a relay or router in front of another provider often forwards
        // them to a backend that does not understand them and answers "upstream 400: the model
        // provider rejected the request". So they are sent, and dropped only once an endpoint has
        // actually rejected them — see WithFieldFallback — which keeps them for gateways that do
        // support them rather than guessing from the URL.
        var advanced = IncludeAdvanced;

        return new MessageCreateParams
        {
            Tools = BuildTools(offerTools),
            Model = _agent.Model,
            MaxTokens = _agent.MaxTokens,
            // Adaptive, never a fixed budget — the budget form is rejected by current models.
            // Disabled has to be sent explicitly: omitting the field leaves thinking on. Left unset
            // entirely once the endpoint has rejected the advanced fields, so the retry is clean.
            Thinking = !advanced
                ? null
                : thinking
                    ? new ThinkingConfigAdaptive()
                    : (ThinkingConfigParam)new ThinkingConfigDisabled(),
            OutputConfig = advanced ? new OutputConfig { Effort = EffortFor(_agent.Effort, thinking) } : null,
            // Cast so the empty branch is a genuine null of the union type. Without it both
            // branches type as the list, and converting a null list into the union throws
            // before the request is ever sent.
            System = string.IsNullOrWhiteSpace(_agent.Instructions)
                ? (MessageCreateParamsSystem?)null
                : new List<TextBlockParam> { new() { Text = _agent.Instructions } },
            Messages = messages,
        };
    }

    /// <summary>
    /// The tools offered this turn: the command and share pair when commands are allowed, and the
    /// image tool when drawing is. Null, not an empty list, when none apply — the API rejects a
    /// tools field that is present but empty.
    /// </summary>
    private List<ToolUnion>? BuildTools(bool offer)
    {
        if (!offer)
        {
            return null;
        }

        var tools = new List<ToolUnion>();

        if (_tools is { Enabled: true })
        {
            tools.Add(new Tool
            {
                Name = AgentTransports.Command.Name,
                Description = AgentTransports.Command.Description,
                InputSchema = new()
                {
                    Properties = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        [AgentTransports.Command.Parameter] = Schema("string", AgentTransports.Command.ParameterDescription),
                        [AgentTransports.Command.Elevated] = Schema("boolean", AgentTransports.Command.ElevatedDescription),
                    },
                    Required = [AgentTransports.Command.Parameter],
                },
            });

            tools.Add(new Tool
            {
                Name = AgentTransports.Share.Name,
                Description = AgentTransports.Share.Description,
                InputSchema = new()
                {
                    Properties = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        [AgentTransports.Share.Parameter] = Schema("string", AgentTransports.Share.ParameterDescription),
                        [AgentTransports.Share.Note] = Schema("string", AgentTransports.Share.NoteDescription),
                    },
                    Required = [AgentTransports.Share.Parameter],
                },
            });

            // The file tools ride the same permission as the command tool.
            foreach (var spec in AgentTransports.Files.All)
            {
                tools.Add(new Tool
                {
                    Name = spec.Name,
                    Description = spec.Description,
                    InputSchema = new()
                    {
                        Properties = spec.Parameters.ToDictionary(p => p.Name, p => Schema(p.Type, p.Description)),
                        Required = [.. spec.Parameters.Where(p => p.Required).Select(p => p.Name)],
                    },
                });
            }
        }

        if (_tools is { ImagesEnabled: true })
        {
            tools.Add(new Tool
            {
                Name = AgentTransports.Image.Name,
                Description = AgentTransports.Image.Description,
                InputSchema = new()
                {
                    Properties = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        [AgentTransports.Image.Parameter] = Schema("string", AgentTransports.Image.ParameterDescription),
                        [AgentTransports.Image.Negative] = Schema("string", AgentTransports.Image.NegativeDescription),
                    },
                    Required = [AgentTransports.Image.Parameter],
                },
            });
        }

        if (_tools is { AgentsEnabled: true })
        {
            tools.Add(new Tool
            {
                Name = AgentTransports.Ask.Name,
                Description = AgentTransports.Ask.Description,
                InputSchema = new()
                {
                    Properties = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        [AgentTransports.Ask.Parameter] = Schema("string", AgentTransports.Ask.ParameterDescription),
                        [AgentTransports.Ask.Request] = Schema("string", AgentTransports.Ask.RequestDescription),
                    },
                    Required = [AgentTransports.Ask.Parameter, AgentTransports.Ask.Request],
                },
            });
        }

        if (_tools is { TasksEnabled: true })
        {
            tools.Add(new Tool
            {
                Name = AgentTransports.Tasks.Name,
                Description = AgentTransports.Tasks.Description,
                InputSchema = new()
                {
                    Properties = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        [AgentTransports.Tasks.Op] = Schema("string", AgentTransports.Tasks.OpDescription),
                        [AgentTransports.Tasks.List] = Schema("string", AgentTransports.Tasks.ListDescription),
                        [AgentTransports.Tasks.Id] = Schema("string", AgentTransports.Tasks.IdDescription),
                        [AgentTransports.Tasks.TaskName] = Schema("string", AgentTransports.Tasks.TaskNameDescription),
                        [AgentTransports.Tasks.Desc] = Schema("string", AgentTransports.Tasks.DescDescription),
                        [AgentTransports.Tasks.State] = Schema("string", AgentTransports.Tasks.StateDescription),
                        [AgentTransports.Tasks.Note] = Schema("string", AgentTransports.Tasks.NoteDescription),
                    },
                    Required = [AgentTransports.Tasks.Op],
                },
            });
        }

        return tools.Count == 0 ? null : tools;
    }

    private static System.Text.Json.JsonElement Schema(string type, string description) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new { type, description });

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
