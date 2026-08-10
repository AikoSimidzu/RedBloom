using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Turns a pasted tool config into ready-to-run agents.
/// </summary>
/// <remarks>
/// The shape it reads is the one command-line Claude tools use: a JSON object with an
/// <c>env</c> block of the environment variables the tool would otherwise be launched with. A
/// bare map of variables works too, since that is what people usually have to hand.
/// <para>
/// Which API an agent gets is taken from the variable names and nothing else. Probing the
/// endpoint was tried and removed: a gateway can answer a plain request with one shape and
/// stream in another — the one measured here returned OpenAI-style JSON to a non-streaming
/// POST while streaming a perfectly correct Anthropic event sequence from the same path. A
/// probe that samples the wrong call therefore talks a working config into the wrong reader,
/// which is worse than trusting what the config says.
/// </para>
/// </remarks>
public static class AgentConfigImport
{
    /// <summary>What an import produced: the agents, or why it produced none.</summary>
    public sealed record Result(IReadOnlyList<AiAgent> Agents, string? Error);

    // The variables worth reading, and what each one means. Names are matched case-insensitively.
    private static readonly string[] BaseUrlKeys =
        ["ANTHROPIC_BASE_URL", "OPENAI_BASE_URL", "OPENAI_API_BASE", "BASE_URL"];

    private static readonly string[] KeyKeys =
        ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "API_KEY"];

    // Model variables paired with the label an agent made from each one carries.
    private static readonly (string Variable, string Label)[] ModelKeys =
    [
        ("ANTHROPIC_MODEL", ""),
        ("ANTHROPIC_DEFAULT_OPUS_MODEL", "opus"),
        ("ANTHROPIC_DEFAULT_SONNET_MODEL", "sonnet"),
        ("ANTHROPIC_DEFAULT_HAIKU_MODEL", "haiku"),
        ("ANTHROPIC_SMALL_FAST_MODEL", "fast"),
        ("OPENAI_MODEL", ""),
        ("MODEL", ""),
    ];

    /// <summary>Reads a config and builds one agent per model it names.</summary>
    public static Result Import(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Result([], "Nothing to import.");
        }

        Dictionary<string, string> variables;
        try
        {
            variables = ReadVariables(text);
        }
        catch (JsonException ex)
        {
            return new Result([], $"That is not valid JSON: {ex.Message}");
        }

        var baseUrl = First(variables, BaseUrlKeys);
        var apiKey = First(variables, KeyKeys);

        var models = ModelKeys
            .Select(m => (m.Label, Model: First(variables, [m.Variable])))
            .Where(m => !string.IsNullOrWhiteSpace(m.Model))
            .DistinctBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (baseUrl is null && apiKey is null && models.Count == 0)
        {
            return new Result([], "No endpoint, key or model was found in that config.");
        }

        var provider = variables.Keys.Any(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase))
            ? AiProvider.Anthropic
            : AiProvider.OpenAiCompatible;

        var host = HostOf(baseUrl);
        var agents = new List<AiAgent>();

        foreach (var (label, model) in models.Count > 0 ? models : [(string.Empty, (string?)null)])
        {
            agents.Add(new AiAgent
            {
                Name = string.IsNullOrEmpty(label) ? host : $"{host} · {label}",
                Provider = provider,
                BaseUrl = NormaliseBaseUrl(baseUrl, provider),
                Model = model ?? AiAgent.DefaultAnthropicModel,
                ApiKey = apiKey,
            });
        }

        return new Result(agents, null);
    }

    /// <summary>Flattens the config into a variable bag, whether or not it has an env block.</summary>
    private static Dictionary<string, string> ReadVariables(string text)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return variables;
        }

        Collect(document.RootElement, variables);

        if (document.RootElement.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            Collect(env, variables);
        }

        return variables;
    }

    private static void Collect(JsonElement element, Dictionary<string, string> into)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { Length: > 0 } value)
            {
                into[property.Name] = value;
            }
        }
    }

    private static string? First(Dictionary<string, string> variables, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Trims the version segment for Anthropic, whose SDK appends its own — a base URL copied
    /// from a tool that spells it out would otherwise reach <c>/v1/v1/messages</c>.
    /// </summary>
    private static string NormaliseBaseUrl(string? baseUrl, AiProvider provider)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');

        return provider == AiProvider.Anthropic && trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3].TrimEnd('/')
            : trimmed;
    }

    private static string HostOf(string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return "Imported";
    }
}
