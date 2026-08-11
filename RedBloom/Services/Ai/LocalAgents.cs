using System.IO;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// The models on this machine, offered alongside the configured agents.
/// </summary>
/// <remarks>
/// Found rather than saved. A local model is a file in the models folder or an entry in the
/// engine's own store, and both come and go without this app being told — writing them into the
/// settings would leave entries pointing at models that had been deleted, and would need keeping
/// in step besides. So the list is built when it is shown, and an agent made this way carries an
/// id derived from the model's name, which is stable enough for a chat to be filed under.
/// </remarks>
public static class LocalAgents
{
    /// <summary>Marks an agent as one of these rather than one the user configured.</summary>
    public const string Prefix = "local:";

    /// <summary>
    /// The models sitting in the folder, known without asking anything.
    /// </summary>
    /// <remarks>
    /// Separate from the full look-round because it is a directory listing and costs nothing,
    /// while asking the engine what it serves means a round trip that takes a moment even when
    /// it refuses. The picker shows these at once and takes the engine's word for it afterwards.
    /// </remarks>
    public static IReadOnlyList<AiAgent> FromFiles()
    {
        var found = new Dictionary<string, AiAgent>(StringComparer.OrdinalIgnoreCase);

        AddFiles(found);

        return [.. found.Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What is on this machine, ready to be chosen like any other agent.</summary>
    public static async Task<IReadOnlyList<AiAgent>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, AiAgent>(StringComparer.OrdinalIgnoreCase);

        // Whatever the engine already serves, whether this app fetched it or the user did.
        foreach (var runner in await LocalRunner.DetectAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!runner.Running)
            {
                continue;
            }

            foreach (var model in runner.Models)
            {
                found[Key(model)] = Build(model, runner.BaseUrl);
            }
        }

        AddFiles(found);

        return [.. found.Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Adds the files the engine has not been introduced to yet. They are offered all the same;
    /// starting a chat is what registers them.
    /// </summary>
    private static void AddFiles(Dictionary<string, AiAgent> found)
    {
        foreach (var file in LocalRunner.Downloaded())
        {
            var name = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();

            // Keyed without the tag the engine appends: a model it already serves as
            // "name:latest" is the same one as the file it was made from, and listing both would
            // offer the user the same model twice under two spellings.
            if (!found.ContainsKey(Key(name)))
            {
                found[Key(name)] = Build(name, $"{LocalRunner.OllamaUrl}/v1");
            }
        }
    }

    /// <summary>A model's identity without the tag the engine appends to it.</summary>
    public static string Key(string model) => model.Split(':')[0];

    /// <summary>
    /// The name given to a model here, or the one it came with.
    /// </summary>
    /// <remarks>
    /// A file downloaded from the hub is called something like
    /// <c>qwythos-9b-claude-mythos-5-1m-uncensored-heretic-q6_k</c>, which says everything about
    /// the build and nothing about what it is for. Renaming changes only the label: the id, the
    /// file, and what the engine is asked for all stay as they were, so chats stay attached and
    /// a rename cannot break the model.
    /// </remarks>
    public static string NameOf(string model) =>
        ThemeService.Settings.LocalModelNames.TryGetValue(Key(model), out var given)
        && !string.IsNullOrWhiteSpace(given)
            ? given
            // Shown without the engine's default tag, which is on everything and so
            // distinguishes nothing. A tag chosen deliberately is kept, because then it does.
            : model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? Key(model) : model;

    /// <summary>Gives a local model a name of its own, or takes it back to the original.</summary>
    public static void Rename(string model, string name)
    {
        var key = Key(model);
        var given = name.Trim();

        if (given.Length == 0)
        {
            ThemeService.Settings.LocalModelNames.Remove(key);
        }
        else
        {
            ThemeService.Settings.LocalModelNames[key] = given;
        }

        ThemeService.Save();
    }

    /// <summary>The agent for one local model, with the identity everything else files under.</summary>
    public static AiAgent For(string model, string baseUrl) => Build(model, baseUrl);

    private static AiAgent Build(string model, string baseUrl) => new()
    {
        Id = Prefix + Key(model),
        Name = NameOf(model),
        Provider = AiProvider.OpenAiCompatible,
        BaseUrl = baseUrl,
        Model = model,

        // Local servers ignore the key, but the transport insists on one being present; an
        // obviously fake value says plainly that nothing secret is kept here.
        ApiKey = "local",
        ContextWindow = 8192,
        Thinking = false,
    };

    /// <summary>
    /// Makes sure the model behind a local agent is actually loaded and answering.
    /// </summary>
    /// <remarks>
    /// Called when a chat opens, because that is the moment the answer matters and the only
    /// moment the user is waiting on it. Starting the engine at launch instead would cost
    /// several seconds and a few gigabytes of memory for everyone who never opens a local chat.
    /// Returns null once the model is ready, or the reason it is not.
    /// </remarks>
    public static async Task<string?> EnsureRunningAsync(
        AiAgent agent, CancellationToken cancellationToken = default)
    {
        if (!agent.IsLocal)
        {
            return null;
        }

        if (await ServesAsync(agent.Model, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Nothing is listening yet. Ollama is tried first: it keeps a store of its own, so a
        // model registered with it stays available afterwards.
        if (OllamaEngine.IsInstalled || OllamaEngine.SystemCopy is not null)
        {
            if (!await OllamaEngine.AnsweringAsync(cancellationToken).ConfigureAwait(false)
                && await OllamaEngine.ServeAsync(cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            if (await ServesAsync(agent.Model, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            // Running, but this model is not in its store yet — introduce the file to it.
            if (FileFor(agent.Model) is { } gguf)
            {
                if (await OllamaEngine.ImportAsync(gguf, cancellationToken).ConfigureAwait(false) is { } complaint)
                {
                    return complaint;
                }

                return await ServesAsync(agent.Model, cancellationToken).ConfigureAwait(false)
                    ? null
                    : LocalizationService.T("L_LocalNotLoaded");
            }

            return LocalizationService.T("L_LocalNoFile");
        }

        // No Ollama at all: llama.cpp's server can host the file directly, if it is installed.
        if (FileFor(agent.Model) is { } file)
        {
            return await LocalRunner.StartAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return LocalizationService.T("L_LocalNoFile");
    }

    /// <summary>Whether some runner is already serving this model.</summary>
    private static async Task<bool> ServesAsync(string model, CancellationToken cancellationToken) =>
        (await LocalRunner.DetectAsync(cancellationToken).ConfigureAwait(false))
            .Any(runner => runner.Running && runner.Models.Any(
                served => Same(served, model)));

    /// <summary>
    /// Ollama lists a model as <c>name:tag</c>, while it is chosen here by name alone.
    /// </summary>
    private static bool Same(string served, string wanted) =>
        served.Equals(wanted, StringComparison.OrdinalIgnoreCase)
        || served.Split(':')[0].Equals(wanted.Split(':')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>The downloaded file a model name came from, if it is still there.</summary>
    private static string? FileFor(string model) =>
        LocalRunner.Downloaded()
            .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file.Name)
                .Equals(model.Split(':')[0], StringComparison.OrdinalIgnoreCase))
            ?.FullName;
}
