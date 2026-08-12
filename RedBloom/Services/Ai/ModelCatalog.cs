using System.Net.Http;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// The models an endpoint says it serves.
/// </summary>
/// <remarks>
/// Asked for rather than hard-coded: the point of pointing an agent at a custom endpoint is that
/// it carries whatever its operator put there, and a built-in list would be wrong for every proxy
/// and stale for the official API within a release or two. Nothing depends on the answer — a
/// model can always be typed by hand — so an endpoint that does not offer the catalogue costs
/// only the convenience of picking from a list.
/// </remarks>
public static class ModelCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>What this agent's endpoint offers, newest first, or empty when it will not say.</summary>
    public static async Task<IReadOnlyList<string>> FetchAsync(
        AiAgent agent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agent.ApiKey))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(agent));

            // Both wire formats are asked the same question; only the way they are addressed
            // differs, exactly as it does for a completion.
            if (agent.Provider == AiProvider.Anthropic)
            {
                request.Headers.Add("x-api-key", agent.ApiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Add("Authorization", $"Bearer {agent.ApiKey}");
            }

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the ids out of a listing, in whichever of the two shapes came back.
    /// </summary>
    /// <remarks>
    /// Both APIs answer with <c>{"data": [{"id": …}]}</c>, but proxies in the wild sometimes
    /// answer with a bare array, so both are accepted rather than making the picker empty over a
    /// pair of brackets.
    /// </remarks>
    private static IReadOnlyList<string> Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<string>();

        foreach (var item in items.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            if (!string.IsNullOrWhiteSpace(id))
            {
                models.Add(id);
            }
        }

        return models;
    }

    /// <summary>
    /// The listing URL, adding the version segment only when the configured base does not
    /// already carry it — proxies are commonly published with it baked in.
    /// </summary>
    private static string Endpoint(AiAgent agent)
    {
        var root = agent.ResolvedBaseUrl;

        // Google's Gemini hangs its OpenAI-shaped endpoints off "/v1beta/openai"; the standard case
        // ends in "/v1". Either takes the path directly, a bare root gets the version added.
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
               || root.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)
            ? $"{root}/models"
            : $"{root}/v1/models";
    }
}
