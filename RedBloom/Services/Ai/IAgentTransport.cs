using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>Who said a thing.</summary>
public enum AgentRole
{
    User,
    Assistant,
}

/// <summary>
/// A picture travelling with a message, already encoded the way both wire formats want it.
/// </summary>
/// <remarks>
/// Base64 rather than a path because a picture cannot be handed over as text at all: read as
/// UTF-8 a JPEG arrives as mojibake, which is what the model then dutifully tries to describe.
/// </remarks>
public readonly record struct AgentImage(string MediaType, string Base64);

/// <summary>One turn of the conversation, as it is replayed to the model.</summary>
/// <remarks>
/// The system prompt is not a message here: both wire formats carry it out of band (Anthropic in
/// a top-level field, the OpenAI shape as a leading message), so each transport places it itself.
/// </remarks>
public readonly record struct AgentMessage(
    AgentRole Role,
    string Text,
    IReadOnlyList<AgentImage>? Images = null);

/// <summary>What a transport reports while a reply is being generated.</summary>
public enum AgentEventKind
{
    /// <summary>A fragment of the visible answer. Arrives many times per turn.</summary>
    Text,

    /// <summary>The model started thinking. Carries no content — the raw reasoning is never returned.</summary>
    Thinking,

    /// <summary>The turn finished cleanly. <see cref="AgentEvent.Text"/> holds a usage line.</summary>
    Completed,

    /// <summary>The turn failed, or the endpoint declined it. Text is the reason to show.</summary>
    Failed,

    /// <summary>The model wants to run a command. Text is the command.</summary>
    ToolCall,

    /// <summary>What the command printed, already trimmed for display.</summary>
    ToolResult,

    /// <summary>The user refused a command, or one could not be run. Text says which.</summary>
    ToolRefused,

    /// <summary>
    /// What the endpoint says this turn has cost so far, in <see cref="AgentEvent.Input"/> and
    /// <see cref="AgentEvent.Output"/>.
    /// </summary>
    Usage,

    /// <summary>
    /// What the model is doing at this moment, as a short phrase in
    /// <see cref="AgentEvent.Text"/> — "reading the output", "writing the answer".
    /// </summary>
    Phase,
}

/// <summary>
/// The names for what a model is doing at a given moment.
/// </summary>
/// <remarks>
/// Names, not sentences: a transport has no business knowing which language the window is in, so
/// it says which phase this is and the chat view looks up the wording. That also keeps the phrase
/// in one place instead of repeated across two transports.
/// </remarks>
public static class AgentPhase
{
    public const string Thinking = "thinking";
    public const string Deciding = "deciding";
    public const string Running = "running";
    public const string RunningElevated = "running-elevated";
    public const string ReadingOutput = "reading-output";
    public const string Writing = "writing";
    public const string Sharing = "sharing";
    public const string WrappingUp = "wrapping-up";
}

/// <param name="Input">Prompt tokens counted by the endpoint, on a <see cref="AgentEventKind.Usage"/>.</param>
/// <param name="Output">Tokens written back, on a <see cref="AgentEventKind.Usage"/>.</param>
public readonly record struct AgentEvent(AgentEventKind Kind, string Text, int Input = 0, int Output = 0)
{
    public static AgentEvent OfText(string text) => new(AgentEventKind.Text, text);

    public static AgentEvent Failure(string reason) => new(AgentEventKind.Failed, reason);

    public static AgentEvent Spent(long input, long output) =>
        new(AgentEventKind.Usage, string.Empty, (int)input, (int)output);

    public static AgentEvent Doing(string what) => new(AgentEventKind.Phase, what);
}

/// <summary>
/// One way of reaching a model. Implemented once per wire format, so the terminal-side agent
/// neither knows nor cares which endpoint it is talking to.
/// </summary>
public interface IAgentTransport : IDisposable
{
    /// <summary>
    /// Sends the conversation and streams the reply back as it is produced.
    /// </summary>
    /// <remarks>
    /// Streaming rather than a single result on purpose: a long answer on a non-streaming request
    /// sits behind the HTTP timeout with nothing to show, and the point of a terminal agent is
    /// that text appears while it is being written.
    /// </remarks>
    IAsyncEnumerable<AgentEvent> SendAsync(
        IReadOnlyList<AgentMessage> conversation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the endpoint answers and the key is accepted, without starting a session.
    /// Returns null when everything is fine, or the reason it is not.
    /// </summary>
    Task<string?> TestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Where a transport goes to have a command approved and run.
/// </summary>
/// <remarks>
/// Running commands is the terminal's business, not the transport's: only the session knows how
/// to put the question to the user and where the answer comes from. A transport that is handed
/// no host, or one that is switched off, simply offers the model no tool.
/// </remarks>
public interface IAgentToolHost
{
    /// <summary>False when this agent may not run commands, in which case no tool is offered.</summary>
    bool Enabled { get; }

    /// <summary>Puts the command to the user. False means it must not run.</summary>
    Task<bool> ApproveAsync(string command, bool elevated, CancellationToken cancellationToken);

    /// <summary>Runs an approved command and returns everything it printed.</summary>
    Task<string> RunAsync(string command, bool elevated, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a file the agent produced into the chat, and reports back whether it arrived.
    /// </summary>
    Task<string> ShareAsync(string path, string note, CancellationToken cancellationToken);
}

/// <summary>Builds the transport an agent's provider calls for.</summary>
public static class AgentTransports
{
    public static IAgentTransport For(AiAgent agent, IAgentToolHost? tools = null) => agent.Provider switch
    {
        AiProvider.Anthropic => new AnthropicTransport(agent, tools),
        AiProvider.OpenAiCompatible => new OpenAiCompatibleTransport(agent, tools),
        _ => throw new ArgumentOutOfRangeException(nameof(agent)),
    };

    /// <summary>
    /// Handing a file back to the user, described the same way to both APIs.
    /// </summary>
    /// <remarks>
    /// Separate from the command tool because it is not a command: the agent has already written
    /// or found the file, and this only says "here, look at this one". Without it a produced file
    /// is a path buried in a paragraph, which the user then has to copy into Explorer.
    /// </remarks>
    public static class Share
    {
        public const string Name = "share_file";

        public const string Description =
            "Show a file to the user in the chat, where they can open it or find it on disk. Use "
            + "it for anything you have written, converted, downloaded or picked out — a report, "
            + "a patch, a log, an exported picture — instead of only naming its path in the "
            + "answer. The file must already exist. Sharing does not send the file anywhere; it "
            + "puts it in front of the person you are talking to.";

        public const string Parameter = "path";

        public const string ParameterDescription = "Full path of the file to show.";

        public const string Note = "note";

        public const string NoteDescription = "One short line saying what this file is.";
    }

    /// <summary>The command tool, described the same way to both APIs.</summary>
    public static class Command
    {
        public const string Name = "run_command";

        public const string Description =
            "Run a command in a Windows command prompt on the user's machine and return its "
            + "output. The working directory is the user's profile folder, and each call is a "
            + "fresh shell — a `cd` in one call does not carry to the next, so chain with `&&` "
            + "when a command depends on a directory. Output is truncated if very long. Use it "
            + "to inspect the system, read files, and run tools; prefer one precise command over "
            + "several exploratory ones, because every call is shown to the user for approval.";

        public const string Parameter = "command";

        public const string ParameterDescription = "The command line to run.";

        public const string Elevated = "as_administrator";

        public const string ElevatedDescription =
            "Set only when the command genuinely needs administrator rights — writing under "
            + "Program Files, changing a service, editing HKLM. The first such command asks the "
            + "user for consent through Windows, and every one of them is shown to the user "
            + "marked as elevated, so asking for it when it is not needed will get the command "
            + "refused rather than run.";
    }
}
