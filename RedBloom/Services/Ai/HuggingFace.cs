using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace RedBloom.Services.Ai;

/// <summary>One model as the hub lists it.</summary>
public sealed record HubModel(string Id, string Author, long Downloads, long Likes, DateTime Updated)
{
    /// <summary>The part after the slash — what the model is actually called.</summary>
    public string Name => Id.Contains('/') ? Id[(Id.IndexOf('/') + 1)..] : Id;
}

/// <summary>
/// One downloadable model file, with what it would cost to run.
/// </summary>
/// <param name="Parts">
/// How many files the model is split across. More than one means <paramref name="Bytes"/> is
/// their total, not the size of the single file named here.
/// </param>
public sealed partial record HubFile(string Model, string Path, long Bytes, int Parts = 1)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsSplit => Parts > 1;

    /// <summary>
    /// The quantisation, pulled out of the file name — Q4_K_M, IQ2_XXS, BF16.
    /// </summary>
    /// <remarks>
    /// Worth showing because it is the choice being made: the same model at Q4 and at Q8 differ
    /// by roughly double in size and by very little in what they say.
    /// </remarks>
    public string Quantisation =>
        QuantisationTag().Match(System.IO.Path.GetFileNameWithoutExtension(Path)) is { Success: true } tag
            ? tag.Value.ToUpperInvariant()
            : string.Empty;

    public Fit Fit => MachineFit.Rate(Bytes);

    /// <summary>The name without its "-00001-of-00003" tail, which is what groups a split model.</summary>
    public static string SetName(string path) => SplitTail().Replace(path, string.Empty);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<![A-Za-z0-9])(I?Q\d+(_[A-Z0-9]+)*|BF16|F16|F32|TQ\d+_\d+)(?![A-Za-z0-9])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex QuantisationTag();

    [System.Text.RegularExpressions.GeneratedRegex(@"-\d{5}-of-\d{5}(?=\.gguf$)")]
    private static partial System.Text.RegularExpressions.Regex SplitTail();
}

/// <summary>
/// The model hub, browsed for things that will actually run here.
/// </summary>
/// <remarks>
/// Only GGUF is listed. It is the one format the local runners read directly, it carries its own
/// quantisation, and a single file is the whole model — so what is downloaded is what runs, with
/// no conversion step in between. The public API is used unauthenticated: everything reachable
/// this way is already public, and asking for a token to browse would be a barrier for nothing.
/// </remarks>
public static class HuggingFace
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string Api = "https://huggingface.co/api";

    static HuggingFace()
    {
        // The hub asks callers to identify themselves, and answers anonymous traffic with
        // tighter limits.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RedBloom/1.0");
    }

    /// <summary>Models matching a search, most downloaded first.</summary>
    public static async Task<IReadOnlyList<HubModel>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        var url = $"{Api}/models?filter=gguf&sort=downloads&direction=-1&limit=40"
            + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"&search={Uri.EscapeDataString(query.Trim())}");

        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));

            var models = new List<HubModel>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = Text(item, "modelId") is { Length: > 0 } named ? named : Text(item, "id");

                if (id.Length == 0)
                {
                    continue;
                }

                // The listing does not always carry the author, but the id always starts with it.
                var author = Text(item, "author") is { Length: > 0 } credited
                    ? credited
                    : id.Contains('/') ? id[..id.IndexOf('/')] : string.Empty;

                models.Add(new HubModel(
                    id,
                    author,
                    Number(item, "downloads"),
                    Number(item, "likes"),
                    item.TryGetProperty("lastModified", out var when) && when.TryGetDateTime(out var date)
                        ? date
                        : DateTime.MinValue));
            }

            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>The GGUF files inside one model, smallest first.</summary>
    public static async Task<IReadOnlyList<HubFile>> FilesAsync(
        string modelId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync($"{Api}/models/{modelId}/tree/main?recursive=true", cancellationToken)
                    .ConfigureAwait(false));

            // A large model is published in numbered parts, each listed separately. Left as they
            // came, the first part of a 60 GB model would show as 10 GB and be marked as fitting
            // — so the parts are folded into one entry carrying their combined size.
            var sets = new Dictionary<string, (string First, long Bytes, int Parts)>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var path = Text(item, "path");

                if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = HubFile.SetName(path);
                var size = Number(item, "size");

                sets[key] = sets.TryGetValue(key, out var seen)
                    ? (string.CompareOrdinal(path, seen.First) < 0 ? path : seen.First,
                        seen.Bytes + size,
                        seen.Parts + 1)
                    : (path, size, 1);
            }

            return
            [
                .. sets.Values
                    .Select(set => new HubFile(modelId, set.First, set.Bytes, set.Parts))
                    .OrderBy(file => file.Bytes),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Downloads one file, reporting progress, and returns where it landed.
    /// </summary>
    /// <remarks>
    /// Written to a temporary name and moved into place at the end, so an interrupted download
    /// never leaves something that looks like a usable model.
    /// </remarks>
    public static async Task<string?> DownloadAsync(
        HubFile file,
        string folder,
        IProgress<(long Done, long Total)> progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);

        var destination = Path.Combine(folder, file.Name);
        var partial = destination + ".part";

        try
        {
            var url = $"https://huggingface.co/{file.Model}/resolve/main/{file.Path}?download=true";

            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? file.Bytes;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var sink = File.Create(partial))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress.Report((done, total));
                }
            }

            File.Move(partial, destination, overwrite: true);

            return destination;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            Delete(partial);

            return null;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover part file is untidy, not harmful; the next attempt overwrites it.
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}
