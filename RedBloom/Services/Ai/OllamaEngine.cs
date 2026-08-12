using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RedBloom.Services.Ai;

/// <summary>
/// Ollama, kept beside the program rather than installed into the machine.
/// </summary>
/// <remarks>
/// The published zip is used instead of the installer: it needs no administrator, writes nothing
/// outside the app's own folder, and leaves the whole thing portable — copy the folder elsewhere
/// and the engine, its models and the chats all come with it. An Ollama the user installed
/// themselves is used as it is; nothing here goes near it.
/// <para>
/// The engine is only ever fetched when the user asks for it. It is well over a gigabyte, and
/// downloading that on a hunch is not something an app should do to someone's connection.
/// </para>
/// </remarks>
public static class OllamaEngine
{
    private const string LatestRelease = "https://api.github.com/repos/ollama/ollama/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static Process? _serving;

    static OllamaEngine() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RedBloom/1.0");

    /// <summary>Where the engine lives, beside the program.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "AIEngine");

    /// <summary>The engine's own copy of ollama.exe, whether or not it is there yet.</summary>
    public static string Executable => Path.Combine(Folder, "ollama.exe");

    /// <summary>True once the engine has been unpacked here.</summary>
    public static bool IsInstalled => File.Exists(Executable);

    /// <summary>An Ollama the user installed themselves, if there is one on the PATH.</summary>
    public static string? SystemCopy => OnPath("ollama.exe");

    /// <summary>True while a server started from here is still up.</summary>
    public static bool Serving => _serving is { HasExited: false };

    /// <summary>
    /// Installs the engine into <see cref="Folder"/>, reporting progress. Null when it is ready,
    /// otherwise the reason it is not.
    /// </summary>
    public static async Task<string?> InstallAsync(
        IProgress<(long Done, long Total)> progress,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            return null;
        }

        var (url, size, error) = await FindAssetAsync(cancellationToken).ConfigureAwait(false);

        if (error is not null || url is null)
        {
            return error ?? LocalizationService.T("L_EngineNoAsset");
        }

        Directory.CreateDirectory(Folder);
        var archive = Path.Combine(Folder, "ollama-download.zip");

        if (await Fetch.ToFileAsync(url, archive, progress, size, cancellationToken).ConfigureAwait(false) is null)
        {
            return LocalizationService.T("L_EngineNoFinish");
        }

        try
        {
            // Extracted in place: the zip carries ollama.exe alongside the runtime libraries it
            // loads, and separating them would leave an engine that starts and then cannot run
            // anything.
            ZipFile.ExtractToDirectory(archive, Folder, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return string.Format(LocalizationService.T("L_EngineNoUnpack"), ex.Message);
        }
        finally
        {
            Fetch.Delete(archive);
        }

        return IsInstalled
            ? null
            : LocalizationService.T("L_EngineNoExe");
    }

    /// <summary>
    /// Starts the engine, keeping its models in the app's own folder. Null once it answers.
    /// </summary>
    /// <remarks>
    /// <c>OLLAMA_MODELS</c> is what decides where several gigabytes per model end up. Left alone
    /// it is the user's profile, which is not where someone who asked for a portable copy expects
    /// to find them.
    /// </remarks>
    public static async Task<string?> ServeAsync(CancellationToken cancellationToken = default)
    {
        var exe = IsInstalled ? Executable : SystemCopy;

        if (exe is null)
        {
            return LocalizationService.T("L_EngineMissing");
        }

        Directory.CreateDirectory(LocalRunner.ModelsFolder);

        var start = new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder,
        };

        start.Environment["OLLAMA_MODELS"] = LocalRunner.ModelsFolder;
        start.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";

        try
        {
            _serving = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return string.Format(LocalizationService.T("L_EngineNoStart"), ex.Message);
        }

        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await AnsweringAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (_serving is { HasExited: true })
            {
                // Usually because something already holds the port. If that something answers,
                // it is an Ollama and will serve just as well; if not, this really did fail.
                return LocalizationService.T("L_EngineQuit");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return LocalizationService.T("L_EngineSlow");
    }

    /// <summary>Whether an Ollama — this one or the user's own — is listening.</summary>
    public static async Task<bool> AnsweringAsync(CancellationToken cancellationToken = default) =>
        (await LocalRunner.DetectAsync(cancellationToken).ConfigureAwait(false))
            .Any(runner => runner is { Name: "Ollama", Running: true });

    /// <summary>Stops the engine this app started. One the user runs themselves is left alone.</summary>
    public static void Stop()
    {
        try
        {
            if (_serving is { HasExited: false })
            {
                _serving.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone between the check and the kill.
        }

        _serving?.Dispose();
        _serving = null;
    }

    /// <summary>
    /// Registers a downloaded GGUF with the engine under a name, so it can be chosen like any
    /// other model. Null when it worked.
    /// </summary>
    /// <remarks>
    /// Ollama serves models from its own store rather than from arbitrary paths, so a file
    /// fetched from the hub has to be introduced to it once. The file is referenced, not copied.
    /// </remarks>
    public static async Task<string?> ImportAsync(
        string ggufPath, CancellationToken cancellationToken = default)
    {
        var exe = IsInstalled ? Executable : SystemCopy;

        if (exe is null)
        {
            return LocalizationService.T("L_EngineMissing");
        }

        if (!File.Exists(ggufPath))
        {
            return LocalizationService.T("L_LocalGone");
        }

        var name = Path.GetFileNameWithoutExtension(ggufPath).ToLowerInvariant();
        var recipe = Path.Combine(Path.GetTempPath(), $"redbloom-{Guid.NewGuid():n}.modelfile");

        try
        {
            await File.WriteAllTextAsync(recipe, $"FROM \"{ggufPath}\"\n", cancellationToken)
                .ConfigureAwait(false);

            var start = new ProcessStartInfo(exe, $"create \"{name}\" -f \"{recipe}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Folder,
            };

            start.Environment["OLLAMA_MODELS"] = LocalRunner.ModelsFolder;
            start.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";

            using var process = Process.Start(start);

            if (process is null)
            {
                return LocalizationService.T("L_EngineNoRun");
            }

            var complaint = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 ? null : complaint.Trim();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
        finally
        {
            Fetch.Delete(recipe);
        }
    }

    /// <summary>
    /// Takes a model out of the engine's own store, so it stops being served and stops being
    /// offered. Null when it worked, and null too when there is no engine to ask — there is then
    /// nothing of ours holding the model, which is the outcome the caller wanted.
    /// </summary>
    /// <remarks>
    /// Deleting the file a model came from is not enough on its own: once it has been imported, the
    /// engine keeps serving it from its store, so discovery finds it again and it lingers in the
    /// list after its file is gone. This is the other half of removing it.
    /// </remarks>
    public static async Task<string?> RemoveAsync(
        string model, CancellationToken cancellationToken = default)
    {
        var exe = IsInstalled ? Executable : SystemCopy;

        if (exe is null)
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(exe, $"rm \"{model}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Folder,
            };

            start.Environment["OLLAMA_MODELS"] = LocalRunner.ModelsFolder;
            start.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";

            using var process = Process.Start(start);

            if (process is null)
            {
                return LocalizationService.T("L_EngineNoRun");
            }

            var complaint = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // A model the store never held reports an error that is not a failure here: the point
            // was for it to be gone, and it is.
            return process.ExitCode == 0 || complaint.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? null
                : complaint.Trim();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }

    /// <summary>The download for this machine's processor, and how big it is.</summary>
    private static async Task<(string? Url, long Size, string? Error)> FindAssetAsync(
        CancellationToken cancellationToken)
    {
        var wanted = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "ollama-windows-arm64.zip"
            : "ollama-windows-amd64.zip";

        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync(LatestRelease, cancellationToken).ConfigureAwait(false));

            if (!document.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return (null, 0, "The release listing was not in the expected shape.");
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name)
                    && name.GetString() == wanted
                    && asset.TryGetProperty("browser_download_url", out var url))
                {
                    var size = asset.TryGetProperty("size", out var bytes) ? bytes.GetInt64() : 0;

                    return (url.GetString(), size, null);
                }
            }

            return (null, 0, $"The latest release does not carry {wanted}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return (null, 0, string.Format(LocalizationService.T("L_EngineListing"), ex.Message));
        }
    }

    private static string? OnPath(string executable)
    {
        if (Environment.GetEnvironmentVariable("PATH") is not { } path)
        {
            return null;
        }

        foreach (var folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), executable);

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

        return null;
    }
}
