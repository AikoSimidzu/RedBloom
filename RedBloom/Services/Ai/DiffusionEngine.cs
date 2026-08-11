using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RedBloom.Services.Ai;

/// <summary>Which build of the engine to fetch — how it does the arithmetic.</summary>
public enum DiffusionBackend
{
    /// <summary>Runs on the processor. Slow, but works on any machine.</summary>
    Cpu,

    /// <summary>Runs on the GPU through Vulkan — Intel, AMD or NVIDIA alike.</summary>
    Vulkan,

    /// <summary>Runs on an NVIDIA GPU through CUDA, the fastest where it applies.</summary>
    Cuda,
}

/// <summary>
/// stable-diffusion.cpp, kept beside the program the same way the LLM engine is.
/// </summary>
/// <remarks>
/// The same bargain as <see cref="OllamaEngine"/>: the published zip is unpacked into the app's
/// own folder, so it needs no administrator, writes nothing outside that folder, and travels with
/// a copied install. Unlike Ollama there is nothing to serve — <c>sd</c> is a command-line tool
/// that <see cref="ImageGen"/> runs once per picture — so this only fetches and unpacks it. It is
/// a few hundred megabytes, so it is fetched only when the user asks.
/// <para>
/// Which build to fetch is a choice: the CPU one runs anywhere, while the Vulkan and CUDA ones run
/// on the GPU and are far faster where the hardware matches. <see cref="Suggested"/> reads the
/// installed display adapters and points at the one that fits, but the choice stays the user's.
/// </para>
/// </remarks>
public static class DiffusionEngine
{
    private const string LatestRelease =
        "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static DiffusionEngine() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RedBloom/1.0");

    /// <summary>Where the engine lives, beside the program and next to the LLM engine.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "AIEngine", "sd");

    /// <summary>Records which build is unpacked here, so a later switch knows what it is replacing.</summary>
    private static string MarkerFile => Path.Combine(Folder, "backend.txt");

    /// <summary>
    /// The command-line tool: sd-cli.exe in current releases, sd.exe in older ones. sd-server.exe
    /// is deliberately not accepted — it is the HTTP server, not what a one-shot run drives.
    /// </summary>
    private static readonly string[] BinaryNames = ["sd-cli.exe", "sd.exe", "stable-diffusion.exe"];

    /// <summary>
    /// This install's own command-line tool, or null when it is not there yet.
    /// </summary>
    /// <remarks>
    /// Found by search rather than as a fixed path: the release zips of different backends unpack
    /// with slightly different layouts, some flat and some inside a subfolder, so where the binary
    /// lands is not known until it is unpacked.
    /// </remarks>
    public static string? Executable
    {
        get
        {
            try
            {
                if (!Directory.Exists(Folder))
                {
                    return null;
                }

                return new DirectoryInfo(Folder)
                    .EnumerateFiles("*.exe", SearchOption.AllDirectories)
                    .Select(f => f.FullName)
                    .FirstOrDefault(path => BinaryNames.Contains(
                        Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>True once a build has been unpacked here.</summary>
    public static bool IsInstalled => Executable is not null;

    /// <summary>Which build is unpacked here, or null when none is.</summary>
    public static DiffusionBackend? InstalledBackend
    {
        get
        {
            try
            {
                return IsInstalled && File.Exists(MarkerFile)
                    && Enum.TryParse<DiffusionBackend>(File.ReadAllText(MarkerFile).Trim(), out var backend)
                    ? backend
                    : IsInstalled ? DiffusionBackend.Cpu : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return IsInstalled ? DiffusionBackend.Cpu : null;
            }
        }
    }

    /// <summary>An sd.exe the user unpacked themselves, if one is on the PATH.</summary>
    public static string? SystemCopy => OnPath("sd-cli.exe") ?? OnPath("sd.exe");

    /// <summary>
    /// The build that fits this machine's graphics: CUDA for an NVIDIA card, Vulkan for an Intel
    /// Arc or AMD one, and the processor otherwise.
    /// </summary>
    /// <remarks>
    /// A suggestion, not a decision — the user can pick another. A weak integrated Intel chip is
    /// left on CPU on purpose: Vulkan on it is often slower than the AVX build, where an Arc is
    /// worth the switch.
    /// </remarks>
    public static DiffusionBackend Suggested()
    {
        var adapters = string.Join(" ", GpuNames()).ToLowerInvariant();

        if (adapters.Contains("nvidia", StringComparison.Ordinal)
            || adapters.Contains("geforce", StringComparison.Ordinal)
            || adapters.Contains("rtx", StringComparison.Ordinal)
            || adapters.Contains("quadro", StringComparison.Ordinal))
        {
            return DiffusionBackend.Cuda;
        }

        if (adapters.Contains("arc", StringComparison.Ordinal)
            || adapters.Contains("radeon", StringComparison.Ordinal)
            || adapters.Contains(" rx ", StringComparison.Ordinal))
        {
            return DiffusionBackend.Vulkan;
        }

        return DiffusionBackend.Cpu;
    }

    /// <summary>
    /// Installs the chosen build into <see cref="Folder"/>, replacing another that is there.
    /// Reports progress; returns null when it is ready, otherwise the reason it is not.
    /// </summary>
    public static async Task<string?> InstallAsync(
        DiffusionBackend backend,
        IProgress<(long Done, long Total)> progress,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled && InstalledBackend == backend)
        {
            return null;
        }

        var (url, size, error) = await FindAssetAsync(backend, cancellationToken).ConfigureAwait(false);

        if (error is not null || url is null)
        {
            return error ?? LocalizationService.T("L_EngineNoAsset");
        }

        // A previous backend's files are cleared first, so switching does not leave both sets of
        // libraries in the folder for the loader to trip over.
        Clear();
        Directory.CreateDirectory(Folder);
        var archive = Path.Combine(Folder, "sd-download.zip");

        if (await Fetch.ToFileAsync(url, archive, progress, size, cancellationToken).ConfigureAwait(false) is null)
        {
            return LocalizationService.T("L_EngineNoFinish");
        }

        try
        {
            // Unpacked in place: sd-cli.exe loads ggml and stable-diffusion DLLs shipped beside it,
            // so splitting them off would leave an engine that starts and then cannot run anything.
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

        if (!IsInstalled)
        {
            return LocalizationService.T("L_ImageEngineNoExe");
        }

        Mark(backend);
        return null;
    }

    private static void Mark(DiffusionBackend backend)
    {
        try
        {
            File.WriteAllText(MarkerFile, backend.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the marker only costs a needless reinstall if the backend is switched later.
        }
    }

    private static void Clear()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file held open by something else; the extract below overwrites what it can.
        }
    }

    /// <summary>
    /// The Windows build for this machine's processor and the chosen backend, and how big it is.
    /// </summary>
    /// <remarks>
    /// The releases carry a zip per backend rather than one canonical name, so the pick is by
    /// scoring against the requested backend, skipping the <c>cudart</c> runtime package that
    /// carries no engine of its own.
    /// </remarks>
    private static async Task<(string? Url, long Size, string? Error)> FindAssetAsync(
        DiffusionBackend backend, CancellationToken cancellationToken)
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        try
        {
            using var document = JsonDocument.Parse(
                await Http.GetStringAsync(LatestRelease, cancellationToken).ConfigureAwait(false));

            if (!document.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return (null, 0, "The release listing was not in the expected shape.");
            }

            string? best = null;
            long bestSize = 0;
            var bestScore = int.MinValue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement)
                    || nameElement.GetString() is not { } name
                    || !asset.TryGetProperty("browser_download_url", out var url))
                {
                    continue;
                }

                var score = Score(name.ToLowerInvariant(), arm, backend);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = url.GetString();
                    bestSize = asset.TryGetProperty("size", out var bytes) ? bytes.GetInt64() : 0;
                }
            }

            return best is null || bestScore < 0
                ? (null, 0, LocalizationService.T("L_EngineNoAsset"))
                : (best, bestSize, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return (null, 0, string.Format(LocalizationService.T("L_EngineListing"), ex.Message));
        }
    }

    /// <summary>How well an asset name fits the backend: higher is better, negative means "not this one".</summary>
    private static int Score(string name, bool arm, DiffusionBackend backend)
    {
        if (!name.EndsWith(".zip", StringComparison.Ordinal)
            || !(name.Contains("win", StringComparison.Ordinal)
                 || name.Contains("windows", StringComparison.Ordinal)))
        {
            return -1;
        }

        // The "cudart" archive is the CUDA runtime DLLs that accompany the GPU build, not the
        // engine — it carries no binary, so installing it would leave nothing to run.
        if (name.Contains("cudart", StringComparison.Ordinal))
        {
            return -1;
        }

        var isArm = name.Contains("arm64", StringComparison.Ordinal);

        // A build for the wrong processor cannot run here at all.
        if (arm != isArm)
        {
            return -1;
        }

        var hasVulkan = name.Contains("vulkan", StringComparison.Ordinal);
        var hasCuda = name.Contains("cuda", StringComparison.Ordinal);
        var hasRocm = name.Contains("rocm", StringComparison.Ordinal)
                      || name.Contains("hip", StringComparison.Ordinal);
        var gpu = hasVulkan || hasCuda || hasRocm
                  || name.Contains("sycl", StringComparison.Ordinal)
                  || name.Contains("musa", StringComparison.Ordinal);

        // The requested backend must match: a Vulkan build is only wanted when Vulkan was asked
        // for, and so on. A mismatch is rejected rather than installed as a slow surprise.
        switch (backend)
        {
            case DiffusionBackend.Vulkan when !hasVulkan:
            case DiffusionBackend.Cuda when !hasCuda:
            case DiffusionBackend.Cpu when gpu:
                return -1;
        }

        var score = 100;

        if (backend == DiffusionBackend.Cpu)
        {
            if (name.Contains("cpu", StringComparison.Ordinal))
            {
                score += 50;
            }
            else if (name.Contains("avx2", StringComparison.Ordinal))
            {
                score += 12;
            }
            else if (name.Contains("avx512", StringComparison.Ordinal))
            {
                score += 8;
            }
            else if (name.Contains("avx", StringComparison.Ordinal))
            {
                score += 6;
            }
        }

        if (name.Contains("x64", StringComparison.Ordinal) || name.Contains("amd64", StringComparison.Ordinal))
        {
            score += 3;
        }

        return score;
    }

    /// <summary>The display adapters Windows knows about, by name.</summary>
    private static IEnumerable<string> GpuNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };

        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if (!string.IsNullOrWhiteSpace(device.DeviceString) && seen.Add(device.DeviceString))
            {
                yield return device.DeviceString;
            }

            device.cb = Marshal.SizeOf<DisplayDevice>();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
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
