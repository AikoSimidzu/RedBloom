using System.ComponentModel;
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
    private bool _askBeforeRun = true;
    private string? _protectedApiKey;

    /// <summary>
    /// The model a new Anthropic agent starts on. Anthropic's most capable general model at the
    /// time of writing; the field is free text, so any other id can be typed in its place.
    /// </summary>
    public const string DefaultAnthropicModel = "claude-opus-5";

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

    /// <summary>How the picker names it — local models are marked so the source is never a guess.</summary>
    [JsonIgnore]
    public string PickerName => IsLocal ? $"[L]  {_name}" : _name;

    /// <summary>
    /// Whether the chat may offer a choice of model.
    /// </summary>
    /// <remarks>
    /// Only agents pointed at an endpoint that serves several. A local agent is the one model
    /// loaded into the engine, and the command-line tool picks for itself from whatever the
    /// signed-in plan allows — in both cases a list would offer choices that cannot be honoured.
    /// </remarks>
    [JsonIgnore]
    public bool CanChooseModel => !IsLocal && _provider is AiProvider.Anthropic or AiProvider.OpenAiCompatible;

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
    /// How much the model can hold at once, used for the gauge and for deciding when to compact.
    /// </summary>
    /// <remarks>
    /// A setting rather than something read from the endpoint: the models endpoint reports it
    /// only on Anthropic's own API, and a proxy in front of a model is free to allow less than
    /// the model itself would.
    /// </remarks>
    public int ContextWindow
    {
        get => _contextWindow;
        set => Set(ref _contextWindow, Math.Clamp(value, 8000, 2_000_000));
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
    public string Origin => _provider == AiProvider.ClaudeCli ? "Claude Code" : ResolvedBaseUrl;

    public string ResolvedBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl)
            ? Provider == AiProvider.Anthropic ? "https://api.anthropic.com" : "https://api.openai.com"
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
        AskBeforeRun = other.AskBeforeRun;

        // Copied by value: a running session must not be able to widen the saved agent's
        // allowances by editing a list it happens to share with it.
        AllowedCommands = [.. other.AllowedCommands];

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
