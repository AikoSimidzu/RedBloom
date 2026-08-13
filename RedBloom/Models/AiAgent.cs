using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using RedBloom.Services;

namespace RedBloom.Models;

/// <summary>Which wire protocol an agent's endpoint speaks.</summary>
public enum AiProvider
{
    /// <summary>Anthropic's own Messages API, driven through the official SDK.</summary>
    Anthropic,

    /// <summary>
    /// The <c>/v1/chat/completions</c> shape, which most proxies, local runners and third-party
    /// providers implement.
    /// </summary>
    OpenAiCompatible,

    /// <summary>
    /// The installed Claude Code command-line tool, driven as a program rather than an endpoint.
    /// </summary>
    /// <remarks>
    /// It signs in through the website and keeps its own credentials, so an agent on this
    /// provider needs no key and no base URL.
    /// </remarks>
    ClaudeCli,

    /// <summary>
    /// A local diffusion model driven through stable-diffusion.cpp, which draws a picture from
    /// each message rather than answering in words.
    /// </summary>
    /// <remarks>
    /// Not an endpoint at all: there is no base URL, no key and no token budget. The message is
    /// the prompt, and the reply is the picture. <see cref="AiAgent.Model"/> names the diffusion
    /// GGUF to load, empty for whichever one is found in the models folder.
    /// </remarks>
    ImageGen,

    /// <summary>
    /// Google's Gemini, reached through its OpenAI-compatible endpoint.
    /// </summary>
    /// <remarks>
    /// Not a separate wire format: Google publishes an OpenAI-shaped endpoint for Gemini, so this
    /// rides the same transport as any other OpenAI-compatible provider — it differs only in the
    /// address it defaults to and the models it lists. Antigravity, which runs on Gemini models, is
    /// configured as one of these.
    /// </remarks>
    Gemini,
}

/// <summary>
/// One configured agent: where to reach a model, which model, and how it should behave.
/// </summary>
/// <remarks>
/// Agents are saved in <c>settings.json</c> alongside everything else, so the API key is not
/// written as it stands — it is wrapped by <see cref="Secrets"/> (DPAPI, current user) exactly
/// as saved SSH passwords are, and only the wrapped form has a serialised property.
/// </remarks>
public sealed class AiAgent : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("n");
    private string _name = "Agent";
    private AiProvider _provider = AiProvider.Anthropic;
    private string _baseUrl = string.Empty;
    private string _model = DefaultAnthropicModel;
    private string _systemPrompt = string.Empty;
    private int _maxTokens = 16000;
    private int _contextWindow = 200000;
    private string _effort = "high";
    private bool _thinking = true;
    private string _avatarPath = string.Empty;
    private string _nameColor = string.Empty;
    private bool _allowCommands;
    private bool _allowImages;
    private bool _allowAgents;
    private bool _askBeforeRun = true;
    private bool _isRemoteLocal;
    private bool _isRoleplay;
    private string _tunnelSessionId = string.Empty;
    private string? _protectedApiKey;
    private string _approvedExample = string.Empty;

    /// <summary>
    /// The model a new Anthropic agent starts on. Anthropic's most capable general model at the
    /// time of writing; the field is free text, so any other id can be typed in its place.
    /// </summary>
    public const string DefaultAnthropicModel = "claude-opus-5";

    /// <summary>
    /// The model a new Gemini agent starts on, a capable general default; the picker offers the
    /// rest, so any other id — a newer Gemini, or an Antigravity one — can replace it.
    /// </summary>
    public const string DefaultGeminiModel = "gemini-2.5-pro";

    /// <summary>Stable identity, so a rename does not orphan the running tabs that use it.</summary>
    public string Id { get => _id; set => Set(ref _id, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// True for a model found on this machine rather than an agent the user configured.
    /// </summary>
    /// <remarks>
    /// Read off the id rather than stored: these are discovered afresh each time the list is
    /// shown, so a saved flag would be one more thing that could disagree with reality.
    /// </remarks>
    [JsonIgnore]
    public bool IsLocal => _id.StartsWith("local:", StringComparison.Ordinal);

    /// <summary>
    /// The model's short name, with the vendor prefix and any path dropped — <c>opus-5</c> from
    /// <c>claude-opus-5</c>, <c>gpt-5</c> from <c>openai/gpt-5</c>. Used to tell copies apart.
    /// </summary>
    [JsonIgnore]
    public string ShortModel
    {
        get
        {
            var model = _model.Trim();

            if (model.Length == 0)
            {
                return string.Empty;
            }

            var slash = model.LastIndexOf('/');
            if (slash >= 0)
            {
                model = model[(slash + 1)..];
            }

            foreach (var prefix in (string[])["claude-", "gpt-", "gemini-", "models/"])
            {
                if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    model = model[prefix.Length..];
                    break;
                }
            }

            return model;
        }
    }

    /// <summary>
    /// What to show for this agent: its plain name when it stands alone, or <c>name [model]</c> when
    /// it is one of several copies sharing a name — so a "multiplied" agent's models are told apart.
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var shared = Services.ThemeService.Settings.Agents
                .Any(a => a.Id != _id && string.Equals(a.Name, _name, StringComparison.Ordinal));

            return shared && ShortModel.Length > 0 ? $"{_name} [{ShortModel}]" : _name;
        }
    }

    /// <summary>How the picker names it — local models are marked so the source is never a guess.</summary>
    [JsonIgnore]
    public string PickerName => IsLocal ? $"[L]  {DisplayName}" : DisplayName;

    /// <summary>
    /// Whether the chat may offer a choice of model.
    /// </summary>
    /// <remarks>
    /// Only agents pointed at an endpoint that serves several. A local agent is the one model
    /// loaded into the engine, and the command-line tool picks for itself from whatever the
    /// signed-in plan allows — in both cases a list would offer choices that cannot be honoured.
    /// </remarks>
    [JsonIgnore]
    public bool CanChooseModel =>
        !IsLocal && !_isRemoteLocal
        && _provider is AiProvider.Anthropic or AiProvider.OpenAiCompatible or AiProvider.Gemini;

    /// <summary>
    /// A model running on another machine of the user's own, reached over the network.
    /// </summary>
    /// <remarks>
    /// Told apart from a hosted endpoint because it behaves like the ones found on this machine:
    /// one model is loaded and that is the one that answers, so there is no list to offer.
    /// </remarks>
    public bool IsRemoteLocal { get => _isRemoteLocal; set => Set(ref _isRemoteLocal, value); }

    /// <summary>True when this agent plays a character rather than assisting.</summary>
    public bool IsRoleplay { get => _isRoleplay; set => Set(ref _isRoleplay, value); }

    /// <summary>Who it plays, when it does.</summary>
    public CharacterCard Character { get; set; } = new();

    /// <summary>
    /// The standing instructions actually sent: the character card first, then anything typed.
    /// </summary>
    /// <remarks>
    /// The card leads because its job is to say who the model is before it can reach for the
    /// identity it memorised in training, and these models weight the opening of the system
    /// message heavily.
    /// </remarks>
    [JsonIgnore]
    public string Instructions
    {
        get
        {
            var card = _isRoleplay ? Character.Compose() : string.Empty;

            var basis = card.Length > 0 && !string.IsNullOrWhiteSpace(_systemPrompt)
                ? card + "\n\n" + _systemPrompt.Trim()
                : card.Length > 0 ? card : _systemPrompt;

            var learned = Learned();

            var body = learned.Length == 0
                ? basis
                : string.IsNullOrWhiteSpace(basis) ? learned : basis + "\n\n" + learned;

            // The environment preamble leads: it tells the model what machine it is on and how to
            // use its tools, which is the operational frame the rest of the prompt sits inside. Set
            // fresh by the chat view before each send, never saved, so it never staleness-rots.
            return string.IsNullOrWhiteSpace(EnvironmentPreamble)
                ? body
                : string.IsNullOrWhiteSpace(body) ? EnvironmentPreamble : EnvironmentPreamble + "\n\n" + body;
        }
    }

    /// <summary>
    /// The machine-and-tools preamble the chat view sets before each send, placed at the head of
    /// <see cref="Instructions"/>. Transient — never serialised — because it describes the run, not
    /// the agent, and it carries the live working directory.
    /// </summary>
    [JsonIgnore]
    public string EnvironmentPreamble { get; set; } = string.Empty;

    /// <summary>
    /// What the thumbs-up and thumbs-down have taught this agent, written as standing guidance.
    /// </summary>
    /// <remarks>
    /// This is how feedback rewards the agent without retraining it: a disliked answer becomes a
    /// thing to avoid, and the most recent liked answer becomes the example to match. Placed last,
    /// after the persona and the typed prompt, because it is a correction to how they are carried
    /// out rather than a change to who the agent is.
    /// </remarks>
    private string Learned()
    {
        var parts = new List<string>();

        if (Lessons.Count > 0)
        {
            var lines = string.Join("\n", Lessons.TakeLast(12).Select(lesson => "- " + lesson));
            parts.Add("The user has corrected earlier answers. Keep to these:\n" + lines);
        }

        if (!string.IsNullOrWhiteSpace(_approvedExample))
        {
            parts.Add("The user marked this earlier answer of yours as good. Match its tone, length "
                + "and approach:\n\"\"\"\n" + _approvedExample.Trim() + "\n\"\"\"");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Things to avoid, gathered from thumbs-down feedback with a reason. Newest matters most; the
    /// prompt keeps only the last handful so the guidance does not grow without bound.
    /// </summary>
    public List<string> Lessons { get; set; } = [];

    /// <summary>The most recent answer the user marked good, kept as an example to match.</summary>
    public string ApprovedExample { get => _approvedExample; set => Set(ref _approvedExample, value); }

    public AiProvider Provider { get => _provider; set => Set(ref _provider, value); }

    /// <summary>
    /// The endpoint root, empty for the provider's own. Anything up to but not including the
    /// version segment — the transports append the rest of the path themselves.
    /// </summary>
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }

    public string Model { get => _model; set => Set(ref _model, value); }

    /// <summary>Standing instructions, sent ahead of the conversation on every turn.</summary>
    public string SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value); }

    /// <summary>Ceiling on one reply. Counts thinking as well as visible text.</summary>
    public int MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, Math.Clamp(value, 256, 128000)); }

    /// <summary>
    /// How much of the conversation to send at a time — the gauge reads against it, compaction
    /// folds to keep under it, and the history is trimmed down to it before each request.
    /// </summary>
    /// <remarks>
    /// A setting rather than something read from the endpoint: the models endpoint reports it
    /// only on Anthropic's own API, and a proxy in front of a model is free to allow less than
    /// the model itself would. The floor is low on purpose — a small local model that only holds a
    /// couple of thousand tokens is set down here so what is sent actually fits it.
    /// </remarks>
    public int ContextWindow
    {
        get => _contextWindow;
        set => Set(ref _contextWindow, Math.Clamp(value, 1024, 2_000_000));
    }

    /// <summary>
    /// How much thinking and work a turn is worth: low, medium, high, xhigh or max. Anthropic
    /// only — the OpenAI-compatible shape has no equivalent, and its transport ignores this.
    /// </summary>
    public string Effort { get => _effort; set => Set(ref _effort, value); }

    /// <summary>
    /// Whether the model may think before answering. Anthropic only, and adaptive when on: the
    /// model decides per turn how much thinking a question is worth, so there is no budget here.
    /// </summary>
    public bool Thinking { get => _thinking; set => Set(ref _thinking, value); }

    /// <summary>
    /// A picture to show beside this agent's replies. Empty for the drawn default.
    /// </summary>
    /// <remarks>
    /// Kept as a path rather than as the image itself: the settings file stays readable, and a
    /// picture the user later edits in place is picked up on the next session without them
    /// having to choose it again.
    /// </remarks>
    public string AvatarPath { get => _avatarPath; set => Set(ref _avatarPath, value); }

    /// <summary>
    /// The colour this agent's name is written in above its replies. Empty follows the accent.
    /// </summary>
    public string NameColor { get => _nameColor; set => Set(ref _nameColor, value); }

    /// <summary>
    /// Whether this agent is offered a tool for running commands on the machine.
    /// </summary>
    /// <remarks>
    /// Off unless it is turned on for a particular agent. An agent that can run commands can do
    /// anything the signed-in user can, and the model deciding what to run is influenced by
    /// whatever it has read along the way — so this is opt-in per agent rather than a global
    /// setting, and <see cref="AskBeforeRun"/> stands between the decision and the machine.
    /// </remarks>
    public bool AllowCommands { get => _allowCommands; set => Set(ref _allowCommands, value); }

    /// <summary>
    /// Whether this agent is offered a tool for drawing pictures with the local image model.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AllowCommands"/>: drawing a picture runs a diffusion model, not an
    /// arbitrary command line, so an agent can be trusted to draw without being trusted to run
    /// anything on the machine. Off unless turned on for a particular agent.
    /// </remarks>
    public bool AllowImages { get => _allowImages; set => Set(ref _allowImages, value); }

    /// <summary>
    /// Whether this agent is offered a tool for handing requests to other configured agents.
    /// </summary>
    /// <remarks>
    /// How an assistant reaches a specialist it is not — an image agent most of all. The agent it
    /// calls runs without tools of its own, so a call cannot spiral into agents calling agents
    /// without end.
    /// </remarks>
    public bool AllowAgents { get => _allowAgents; set => Set(ref _allowAgents, value); }

    /// <summary>Whether each command is shown for approval before it runs. On by default.</summary>
    public bool AskBeforeRun { get => _askBeforeRun; set => Set(ref _askBeforeRun, value); }

    /// <summary>
    /// Commands this agent may run without being asked each time.
    /// </summary>
    /// <remarks>
    /// An entry matches a command that is exactly it, or that starts with it followed by a
    /// space — so <c>git</c> waves through every git call, while <c>git status</c> waves through
    /// only that one. The list is per agent and is added to from the approval prompt itself,
    /// which is where the user actually knows whether a command is one they want to keep
    /// allowing.
    /// </remarks>
    public List<string> AllowedCommands { get; set; } = [];

    /// <summary>
    /// This agent's own task list — its private notebook, kept apart from any chat's or room's
    /// shared list.
    /// </summary>
    /// <remarks>
    /// Per agent rather than per conversation: it is the agent's running account of what it is
    /// doing, which the user can look in on at any time and the agent keeps up through the
    /// <c>manage_tasks</c> tool. Persisted with the agent, so a plan survives across chats and runs.
    /// </remarks>
    public List<TaskItem> Tasks { get; set; } = [];

    /// <summary>True when a command is covered by <see cref="AllowedCommands"/>.</summary>
    public bool IsAlwaysAllowed(string command)
    {
        var candidate = command.Trim();

        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (var allowed in AllowedCommands)
        {
            var pattern = allowed.Trim();

            if (pattern.Length == 0)
            {
                continue;
            }

            if (candidate.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(pattern + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The part of a command worth remembering when the user allows it for good: the program,
    /// plus a subcommand when the program is one that does everything through them.
    /// </summary>
    /// <remarks>
    /// Allowing a bare <c>git</c> also allows <c>git push --force</c>, which is rarely what
    /// someone means by "always allow this". For the handful of tools built entirely out of
    /// subcommands, the suggestion therefore keeps the first one.
    /// </remarks>
    public static string SuggestAllowPattern(string command)
    {
        var parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return string.Empty;
        }

        string[] subcommandTools = ["git", "dotnet", "npm", "npx", "pnpm", "yarn", "docker", "winget", "pip", "cargo", "go"];

        return parts.Length > 1
               && subcommandTools.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
               && !parts[1].StartsWith('-')
            ? $"{parts[0]} {parts[1]}"
            : parts[0];
    }

    /// <summary>
    /// A saved SSH connection to carry this agent's traffic, or empty for a direct one.
    /// </summary>
    /// <remarks>
    /// For a model server that listens only on its own machine's loopback. The agent's address
    /// stays what it is — <c>127.0.0.1:8080</c> — and the connection named here is what makes
    /// that address mean the remote machine. See <see cref="Services.Ai.AgentTunnel"/>.
    /// </remarks>
    public string TunnelSessionId { get => _tunnelSessionId; set => Set(ref _tunnelSessionId, value ?? string.Empty); }

    [JsonIgnore]
    public bool UsesTunnel => !string.IsNullOrWhiteSpace(_tunnelSessionId);

    /// <summary>The API key as it is stored and serialised — DPAPI ciphertext, never plaintext.</summary>
    [JsonPropertyName("apiKey")]
    public string? ProtectedApiKey { get => _protectedApiKey; set => Set(ref _protectedApiKey, value); }

    /// <summary>The key in the clear, for the transports and the settings page. Never serialised.</summary>
    [JsonIgnore]
    public string? ApiKey
    {
        get => Secrets.Unprotect(_protectedApiKey);
        set => ProtectedApiKey = Secrets.Protect(value);
    }

    /// <summary>Where this agent's requests actually go, once the default is filled in.</summary>
    /// <summary>
    /// What the chat shows as the source of the answers.
    /// </summary>
    /// <remarks>
    /// Not the base URL for every agent: the command-line tool has none, and showing the
    /// fallback would name an endpoint it never contacts.
    /// </remarks>
    [JsonIgnore]
    public string Origin => _provider switch
    {
        AiProvider.ClaudeCli => "Claude Code",
        AiProvider.ImageGen => "stable-diffusion.cpp",
        _ => ResolvedBaseUrl,
    };

    public string ResolvedBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl)
            ? Provider switch
            {
                AiProvider.Anthropic => "https://api.anthropic.com",
                AiProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
                _ => "https://api.openai.com",
            }
            : BaseUrl.TrimEnd('/');

    public AiAgent Clone()
    {
        var copy = new AiAgent();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Takes every field from <paramref name="other"/>, identity included.</summary>
    public void CopyFrom(AiAgent other)
    {
        Id = other.Id;
        Name = other.Name;
        Provider = other.Provider;
        BaseUrl = other.BaseUrl;
        Model = other.Model;
        SystemPrompt = other.SystemPrompt;
        MaxTokens = other.MaxTokens;
        ContextWindow = other.ContextWindow;
        Effort = other.Effort;
        Thinking = other.Thinking;
        AvatarPath = other.AvatarPath;
        NameColor = other.NameColor;
        AllowCommands = other.AllowCommands;
        AllowImages = other.AllowImages;
        AllowAgents = other.AllowAgents;
        AskBeforeRun = other.AskBeforeRun;

        // Copied by value: a running session must not be able to widen the saved agent's
        // allowances by editing a list it happens to share with it.
        AllowedCommands = [.. other.AllowedCommands];

        TunnelSessionId = other.TunnelSessionId;
        IsRemoteLocal = other.IsRemoteLocal;
        IsRoleplay = other.IsRoleplay;
        Character.CopyFrom(other.Character);

        // Copied by value, like the allowances: a running session must not widen the saved agent's
        // learned guidance by editing a list it happens to share.
        Lessons = [.. other.Lessons];
        ApprovedExample = other.ApprovedExample;

        // Moved in its wrapped form: unwrapping and rewrapping would be pointless work and
        // would put the key in a string for no reason.
        ProtectedApiKey = other.ProtectedApiKey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on any edit, so the settings page can persist without watching each field.</summary>
    public event Action? Changed;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        Changed?.Invoke();
    }
}
