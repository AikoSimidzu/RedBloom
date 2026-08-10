using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace RedBloom.Services.Ai;

/// <summary>A local server that answers like the OpenAI API, and what it currently serves.</summary>
public sealed record RunnerState(string Name, string BaseUrl, bool Running, IReadOnlyList<string> Models);

/// <summary>
/// Models running on this machine.
/// </summary>
/// <remarks>
/// Nothing here does any inference: that is what Ollama and llama.cpp are for, both of which
/// already speak the OpenAI shape this app has a transport for. So a local model is not a special
/// kind of agent — it is an ordinary agent pointed at <c>127.0.0.1</c>, and everything the chat
/// can already do works with it unchanged.
/// </remarks>
public static class LocalRunner
{
    /// <summary>
    /// Short, because these are both on this machine: a runner that is there answers at once,
    /// and one that is not refuses at once. Anything longer is a pause the user sits through
    /// every time the picker is filled.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

    /// <summary>Where Ollama listens unless it has been told otherwise.</summary>
    public const string OllamaUrl = "http://127.0.0.1:11434";

    /// <summary>llama.cpp's own server, on its default port.</summary>
    public const string LlamaUrl = "http://127.0.0.1:8080";

    private static Process? _started;

    /// <summary>
    /// Where downloaded models are kept, beside the program with the chats and the engine.
    /// </summary>
    /// <remarks>
    /// Also handed to the engine as <c>OLLAMA_MODELS</c>, so a model pulled by Ollama itself
    /// lands here too rather than in the user's profile — otherwise "portable" would hold for the
    /// program and not for the several gigabytes that make it useful.
    /// </remarks>
    public static string ModelsFolder { get; } = Path.Combine(AppContext.BaseDirectory, "AIModels");

    /// <summary>
    /// What is listening right now, and what it offers.
    /// </summary>
    /// <remarks>
    /// Both are asked at once. One after the other meant waiting out the first refusal before
    /// the second was even tried, which doubled a delay the user notices — this runs whenever
    /// the agent list is filled.
    /// </remarks>
    public static async Task<IReadOnlyList<RunnerState>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var ollama = OllamaAsync(cancellationToken);
        var llama = LlamaAsync(cancellationToken);

        await Task.WhenAll(ollama, llama).ConfigureAwait(false);

        return [ollama.Result, llama.Result];
    }

    /// <summary>The GGUF files already downloaded here.</summary>
    public static IReadOnlyList<FileInfo> Downloaded()
    {
        try
        {
            return Directory.Exists(ModelsFolder)
                ? [.. new DirectoryInfo(ModelsFolder).EnumerateFiles("*.gguf").OrderBy(f => f.Name)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Starts llama.cpp's server on a downloaded file. Returns null once it answers, or why not.
    /// </summary>
    /// <remarks>
    /// Only llama-server is started this way, and only if it is already installed — shipping or
    /// silently fetching an inference binary is not something this app should do behind the
    /// user's back. Ollama is left alone: it runs as a service and manages its own models, so
    /// starting a second copy of anything would only fight with it.
    /// </remarks>
    public static async Task<string?> StartAsync(string ggufPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ggufPath))
        {
            return LocalizationService.T("L_LocalGone");
        }

        if (FindLlamaServer() is not { } exe)
        {
            return LocalizationService.T("L_LocalNoLlama");
        }

        Stop();

        try
        {
            _started = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = $"-m \"{ggufPath}\" --port 8080 --host 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return string.Format(LocalizationService.T("L_LocalLlamaFailed"), ex.Message);
        }

        // Loading a model of several gigabytes takes a while; the server does not answer until
        // it is ready, so this waits rather than reporting a failure it would recover from.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (_started is { HasExited: true })
            {
                return LocalizationService.T("L_LocalLlamaQuit");
            }

            if ((await LlamaAsync(cancellationToken).ConfigureAwait(false)).Running)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return LocalizationService.T("L_LocalLlamaSlow");
    }

    /// <summary>Stops the server this app started. One it did not start is left alone.</summary>
    public static void Stop()
    {
        try
        {
            if (_started is { HasExited: false })
            {
                _started.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone between the check and the kill.
        }

        _started?.Dispose();
        _started = null;
    }

    /// <summary>True while a server started from here is still up.</summary>
    public static bool Hosting => _started is { HasExited: false };

    private static string? FindLlamaServer()
    {
        var names = new[] { "llama-server.exe", "server.exe" };

        var folders = new List<string>();

        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            folders.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        // Beside the program, where this app keeps its own engine, and the two places a manually
        // unpacked llama.cpp usually ends up.
        folders.Add(OllamaEngine.Folder);
        folders.Add(Path.Combine(AppContext.BaseDirectory, "llama"));
        folders.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "llama.cpp"));

        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), name);

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry; the rest are still worth trying.
                }
            }
        }

        return null;
    }

    private static async Task<RunnerState> OllamaAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync($"{OllamaUrl}/api/tags", cancellationToken).ConfigureAwait(false));

            var models = new List<string>();

            if (document.RootElement.TryGetProperty("models", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        models.Add(name.GetString() ?? string.Empty);
                    }
                }
            }

            return new RunnerState("Ollama", $"{OllamaUrl}/v1", true, models);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return new RunnerState("Ollama", $"{OllamaUrl}/v1", false, []);
        }
    }

    private static async Task<RunnerState> LlamaAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync($"{LlamaUrl}/v1/models", cancellationToken).ConfigureAwait(false));

            var models = new List<string>();

            if (document.RootElement.TryGetProperty("data", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        models.Add(id.GetString() ?? string.Empty);
                    }
                }
            }

            return new RunnerState("llama.cpp", $"{LlamaUrl}/v1", true, models);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return new RunnerState("llama.cpp", $"{LlamaUrl}/v1", false, []);
        }
    }
}
