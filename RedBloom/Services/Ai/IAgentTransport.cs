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

    /// <summary>
    /// A fragment of the model's own reasoning, where the endpoint returns it.
    /// </summary>
    /// <remarks>
    /// Shown folded away rather than inline: it is working-out, not an answer, and it is often
    /// longer than the reply it leads to. Not every endpoint sends it, so a turn with none is
    /// ordinary rather than a failure.
    /// </remarks>
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

    /// <summary>A picture was produced. <see cref="AgentEvent.Text"/> is its path on disk.</summary>
    Image,

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
    public const string Loading = "loading";
    public const string Tunnelling = "tunnelling";
    public const string Deciding = "deciding";
    public const string Running = "running";
    public const string RunningElevated = "running-elevated";
    public const string Drawing = "drawing";
    public const string Asking = "asking";
    public const string ReadingOutput = "reading-output";
    public const string Writing = "writing";
    public const string Sharing = "sharing";
    public const string WrappingUp = "wrapping-up";
    public const string Reconnecting = "reconnecting";
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

    /// <summary>A finished picture, carried as the path it was written to.</summary>
    public static AgentEvent Image(string path) => new(AgentEventKind.Image, path);
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

    /// <summary>True when this agent may draw pictures, in which case the image tool is offered.</summary>
    bool ImagesEnabled { get; }

    /// <summary>True when this agent may call other agents, in which case the ask tool is offered.</summary>
    bool AgentsEnabled { get; }

    /// <summary>
    /// True when the task tool is offered — it always is for an ordinary chat or room, so the model
    /// can keep the task list and its own notebook current.
    /// </summary>
    bool TasksEnabled { get; }

    /// <summary>Puts the command to the user. False means it must not run.</summary>
    Task<bool> ApproveAsync(string command, bool elevated, CancellationToken cancellationToken);

    /// <summary>Runs an approved command and returns everything it printed.</summary>
    Task<string> RunAsync(string command, bool elevated, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a file the agent produced into the chat, and reports back whether it arrived.
    /// </summary>
    Task<string> ShareAsync(string path, string note, CancellationToken cancellationToken);

    /// <summary>
    /// Draws a picture from a prompt and shows it in the chat, reporting back what happened.
    /// </summary>
    Task<string> GenerateImageAsync(string prompt, string negative, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a request to another named agent and returns what it produced, showing any picture it
    /// drew in the chat.
    /// </summary>
    Task<string> AskAgentAsync(string agentName, string request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads, adds, changes or removes a task on the shared list or the agent's own notebook, and
    /// returns both lists as they stand so the model can see the ids to act on next.
    /// </summary>
    /// <param name="argumentsJson">The tool call's raw arguments, as the model sent them.</param>
    Task<string> ManageTasksAsync(string argumentsJson, CancellationToken cancellationToken);

    /// <summary>
    /// Carries out one of the file tools — read, write, edit or list — and returns the result. A
    /// write or edit is put to the user the same way a command is; a read or a listing is not.
    /// </summary>
    /// <param name="name">Which file tool: <see cref="AgentTransports.Files"/> names.</param>
    /// <param name="argumentsJson">The tool call's raw arguments, as the model sent them.</param>
    Task<string> FileToolAsync(string name, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>Builds the transport an agent's provider calls for.</summary>
public static class AgentTransports
{
    public static IAgentTransport For(AiAgent agent, IAgentToolHost? tools = null) => agent.Provider switch
    {
        AiProvider.Anthropic => new AnthropicTransport(agent, tools),

        // Gemini speaks the OpenAI shape through Google's compatibility endpoint, so it rides the
        // same transport — the provider only changes the default address and the model list.
        AiProvider.OpenAiCompatible or AiProvider.Gemini => new OpenAiCompatibleTransport(agent, tools),

        // No tool host: this one brings its own tools and asks for its own permissions.
        AiProvider.ClaudeCli => new ClaudeCliTransport(agent),

        // No tool host either: it draws rather than talks, so a message is a prompt.
        AiProvider.ImageGen => new ImageGenTransport(agent),
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

    /// <summary>
    /// Drawing a picture from a prompt, described the same way to both APIs.
    /// </summary>
    /// <remarks>
    /// Runs a local diffusion model through <see cref="ImageGen"/> and puts the result in the
    /// chat at full size. Offered only to an agent that has image generation turned on, and needs
    /// no approval — it writes a picture to the user's own machine and shows it to them, the same
    /// standing as <see cref="Share"/>.
    /// </remarks>
    public static class Image
    {
        public const string Name = "generate_image";

        public const string Description =
            "Draw a picture from a text description using the local image model, and show it to "
            + "the user in the chat at full size. Write the prompt the way image models expect: a "
            + "comma-separated list of subject, appearance, setting, style and quality tags rather "
            + "than a sentence. Use this when the user asks you to draw, generate, or show an "
            + "image; do not claim to have made a picture without calling it.";

        public const string Parameter = "prompt";

        public const string ParameterDescription =
            "What to draw, as comma-separated tags: subject, look, setting, style, quality.";

        public const string Negative = "negative";

        public const string NegativeDescription =
            "Optional. Comma-separated tags for what to keep out of the picture.";
    }

    /// <summary>
    /// Handing a request to another configured agent, described the same way to both APIs.
    /// </summary>
    /// <remarks>
    /// The named agent answers on its own — a language agent replies in words, an image agent
    /// draws a picture — and what it produces comes back so this one can use it. It is how an
    /// assistant reaches a specialist it does not embody itself, an image model above all.
    /// </remarks>
    public static class Ask
    {
        public const string Name = "ask_agent";

        public const string Description =
            "Send a request to another of the user's configured agents and get what it produces "
            + "back. Use it to reach a capability you do not have yourself — above all an image "
            + "agent, which draws a picture from a description and shows it to the user. Name the "
            + "agent exactly as it is configured. The agent you call answers on its own and cannot "
            + "call others in turn, so give it everything it needs in one request.";

        public const string Parameter = "agent";

        public const string ParameterDescription = "The exact name of the agent to ask.";

        public const string Request = "request";

        public const string RequestDescription =
            "What to ask it for — a question for a language agent, or a full image description for "
            + "an image agent.";
    }

    /// <summary>
    /// The task tool, described the same way to both APIs.
    /// </summary>
    /// <remarks>
    /// One tool covers both lists: the shared one the user sees in the header, and the agent's own
    /// notebook. It takes no approval — it changes a list on the user's own screen, the same
    /// standing as sharing a file — and its result is always the current state of both lists, so
    /// the model learns the ids it needs without a separate read.
    /// </remarks>
    public static class Tasks
    {
        public const string Name = "manage_tasks";

        public const string Description =
            "Read and keep task lists that the user can see in the chat header. There are two: "
            + "\"shared\", the list you and the user share, and \"mine\", your own private notebook "
            + "for planning your work. Every call returns both lists with their ids, so call it with "
            + "op \"list\" first to see what is there. Use op \"add\" to create a task (give it a "
            + "name, optionally a description and a state), \"update\" to change a task's name, "
            + "description or state by its id, \"delete\" to remove one by id, and \"report\" to post "
            + "a short progress note to the user (put the note in \"note\"). Keep a task's state "
            + "current as you work: set it to InProgress when you start, Done when finished, "
            + "NeedsRework or Tests when that is where it stands. Valid states: NotStarted, "
            + "InProgress, Done, NeedsRework, Tests.";

        public const string Op = "op";
        public const string OpDescription = "One of: list, add, update, delete, report.";

        public const string List = "list";
        public const string ListDescription = "Which list to act on: \"shared\" or \"mine\". Defaults to shared.";

        public const string Id = "id";
        public const string IdDescription = "The task id, for update and delete. Ids are shown in the result of any call.";

        public const string TaskName = "name";
        public const string TaskNameDescription = "The task's short name, for add and update.";

        public const string Desc = "desc";
        public const string DescDescription = "The task's description, for add and update.";

        public const string State = "state";

        public const string StateDescription =
            "The task's state: NotStarted, InProgress, Done, NeedsRework or Tests.";

        public const string Note = "note";
        public const string NoteDescription = "A short progress note to show the user, for op \"report\".";
    }

    /// <summary>
    /// The file tools — read, write, edit and list — described the same way to both APIs.
    /// </summary>
    /// <remarks>
    /// These are what a model edits code with, instead of hand-rolling echo/Set-Content one-liners
    /// through the shell: exact, not subject to the quoting and encoding traps of a command line,
    /// and not truncated the way <c>type</c> is. Offered together with <see cref="Command"/> — a
    /// write or edit is as powerful as a command, so it rides the same permission and, when the
    /// agent's mode asks, the same approval. Relative paths resolve against the chat's working
    /// directory.
    /// </remarks>
    public static class Files
    {
        public const string Read = "read_file";
        public const string Write = "write_file";
        public const string Edit = "edit_file";
        public const string List = "list_dir";

        public const string Path = "path";
        public const string Content = "content";
        public const string Old = "old";
        public const string New = "new";
        public const string ReplaceAll = "replace_all";
        public const string StartLine = "start_line";
        public const string EndLine = "end_line";

        public static readonly HashSet<string> Names = new(StringComparer.Ordinal) { Read, Write, Edit, List };

        /// <summary>One parameter of a file tool: name, JSON type, description, and whether required.</summary>
        public readonly record struct Param(string Name, string Type, string Description, bool Required);

        /// <summary>One file tool's full description, for a transport to turn into its own schema.</summary>
        public readonly record struct Spec(string Name, string Description, Param[] Parameters);

        /// <summary>Every file tool, so each transport declares them from one source.</summary>
        public static readonly Spec[] All =
        [
            new(Read,
                "Read a file and return its contents, optionally just a range of lines. Use this "
                + "rather than a `type` command: it is not truncated the same way and can return "
                + "exact line numbers to edit against.",
                [
                    new(Path, "string", "Path of the file to read; relative paths resolve against the working directory.", true),
                    new(StartLine, "integer", "Optional. First line to return, 1-based.", false),
                    new(EndLine, "integer", "Optional. Last line to return, 1-based.", false),
                ]),
            new(Write,
                "Create a file, or replace an existing one whole, with the given contents. Makes "
                + "any missing parent folders. Use this to write a new file or rewrite a small one; "
                + "for a change to part of a larger file, prefer edit_file.",
                [
                    new(Path, "string", "Path of the file to write; relative paths resolve against the working directory.", true),
                    new(Content, "string", "The full contents to write.", true),
                ]),
            new(Edit,
                "Replace an exact piece of text in a file with new text. The old text must appear "
                + "in the file exactly once unless replace_all is set. This is the precise way to "
                + "change code — no quoting or escaping problems.",
                [
                    new(Path, "string", "Path of the file to edit; relative paths resolve against the working directory.", true),
                    new(Old, "string", "The exact text to replace, copied from the file including its indentation.", true),
                    new(New, "string", "The text to put in its place.", true),
                    new(ReplaceAll, "boolean", "Optional. Replace every occurrence instead of requiring exactly one.", false),
                ]),
            new(List,
                "List the entries of a folder — files and subfolders. Defaults to the working "
                + "directory when no path is given.",
                [
                    new(Path, "string", "Optional. Folder to list; relative paths resolve against the working directory.", false),
                ]),
        ];
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
