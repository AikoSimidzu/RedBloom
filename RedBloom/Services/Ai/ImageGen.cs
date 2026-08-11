using System.Diagnostics;
using System.IO;
using System.Text;

namespace RedBloom.Services.Ai;

/// <summary>What a generation run produced: the picture, or why there is none.</summary>
public sealed record ImageGenResult(bool Ok, string? PngPath, string Message);

/// <summary>What kind of diffusion model a GGUF is, which decides its defaults and needs.</summary>
public enum DiffusionKind
{
    Unknown,
    Sd15,
    Sdxl,
    Sd3,
    Flux,
}

/// <summary>
/// Knobs a caller may set. Zero on a numeric knob means "decide from the model": the size, steps
/// and guidance an SD1.5 model wants are not what an SDXL or a Flux model wants, and picking one
/// fixed set produces washed-out or empty pictures on the others.
/// </summary>
public sealed record ImageOptions
{
    /// <summary>The diffusion GGUF to load. Empty picks one out of <see cref="ImageGen.ModelsFolder"/>.</summary>
    public string ModelPath { get; init; } = string.Empty;

    public string Negative { get; init; } = string.Empty;

    /// <summary>0 lets the model's kind decide — 1024 for SDXL and Flux, 512 for SD1.5.</summary>
    public int Width { get; init; }

    /// <summary>0 lets the model's kind decide — 1024 for SDXL and Flux, 512 for SD1.5.</summary>
    public int Height { get; init; }

    /// <summary>0 falls back to a sensible step count.</summary>
    public int Steps { get; init; }

    /// <summary>0 lets the model's kind decide — around 7 for SD/SDXL, 1 for Flux.</summary>
    public double CfgScale { get; init; }

    /// <summary>-1 lets the engine pick, so two calls with the same prompt differ.</summary>
    public long Seed { get; init; } = -1;
}

/// <summary>
/// Turns a prompt into a picture by driving stable-diffusion.cpp's <c>sd</c> on a local GGUF
/// diffusion model.
/// </summary>
/// <remarks>
/// The same bargain as <see cref="LocalRunner"/>: the app does not ship or fetch an inference
/// binary. It looks for <c>sd.exe</c> where one is usually unpacked and, finding none, says so and
/// where to put it. The picture is written to a folder that outlives the run, because a chat that
/// referred to it earlier still has to show it when reopened — a temp file swept away between
/// sessions would leave a broken frame in the history.
/// </remarks>
public static class ImageGen
{
    /// <summary>Where the diffusion GGUFs live — the same folder the LLM engine uses.</summary>
    public static string ModelsFolder => LocalRunner.ModelsFolder;

    /// <summary>Where finished pictures are kept, beside the settings rather than in a temp sweep.</summary>
    public static string OutputFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "generated");

    // Recent stable-diffusion.cpp releases ship the command-line tool as sd-cli.exe; older ones
    // called it sd.exe. sd-server.exe is the HTTP server, which is not what one-shot runs use.
    private static readonly string[] BinaryNames = ["sd-cli.exe", "sd.exe", "stable-diffusion.exe"];

    /// <summary>Filename fragments that mark a GGUF as a diffusion model rather than an LLM.</summary>
    private static readonly string[] DiffusionHints =
        ["diffusion", "-xl", "_xl", "sdxl", "pony", "anything", "flux", "sd15", "sd-1", "dreamshaper"];

    /// <summary>The <c>sd</c> binary, or null when none is installed where it is looked for.</summary>
    public static string? FindBinary()
    {
        // The engine RedBloom installed itself wins: it is the one the install button put there,
        // and its layout is known. Anything the user unpacked by hand is found by the folder sweep.
        if (DiffusionEngine.Executable is { } engine)
        {
            return engine;
        }

        foreach (var folder in SearchFolders())
        {
            foreach (var name in BinaryNames)
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

    public static bool Available => FindBinary() is not null;

    /// <summary>The folders searched, so a "not installed" message can name where to drop it.</summary>
    public static IEnumerable<string> SearchFolders()
    {
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return entry;
            }
        }

        // Beside the program, in an "sd" subfolder, and where a manually unpacked build tends to sit.
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "sd");
        yield return ModelsFolder;
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "stable-diffusion.cpp");
    }

    /// <summary>
    /// The diffusion model to use: the one named, or the first GGUF here that looks like a
    /// diffusion model rather than a language model.
    /// </summary>
    public static string? DefaultModel()
    {
        try
        {
            if (!Directory.Exists(ModelsFolder))
            {
                return null;
            }

            return new DirectoryInfo(ModelsFolder)
                .EnumerateFiles("*.gguf")
                .Select(f => f.FullName)
                .FirstOrDefault(LooksLikeDiffusion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool LooksLikeDiffusion(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        return DiffusionHints.Any(hint => name.Contains(hint, StringComparison.Ordinal));
    }

    /// <summary>The diffusion GGUFs in the models folder, for a chooser to list.</summary>
    public static IReadOnlyList<string> DiffusionModels()
    {
        try
        {
            return Directory.Exists(ModelsFolder)
                ? [.. new DirectoryInfo(ModelsFolder)
                    .EnumerateFiles("*.gguf")
                    .Select(f => f.FullName)
                    .Where(LooksLikeDiffusion)
                    .OrderBy(Path.GetFileName)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Turns what an agent stored as its model — a full path, a file name, or nothing — into a
    /// model file that exists, or null when none matches.
    /// </summary>
    public static string? ResolveModel(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return DefaultModel();
        }

        if (File.Exists(nameOrPath))
        {
            return nameOrPath;
        }

        var wanted = Path.GetFileName(nameOrPath);

        try
        {
            if (Directory.Exists(ModelsFolder))
            {
                var candidates = new DirectoryInfo(ModelsFolder).EnumerateFiles("*.gguf");

                return candidates.FirstOrDefault(f =>
                        string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase))?.FullName
                    ?? candidates.FirstOrDefault(f =>
                        f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))?.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static readonly string[] SdxlNameHints =
        ["sdxl", "-xl", "_xl", "pony", "illustrious", "animagine", "noob", "juggernaut"];

    private static readonly string[] Sd15NameHints =
        ["sd15", "sd-1.5", "sd_1.5", "sd1.5", "v1-5", "v1.5"];

    private static readonly string[] VaeHints = ["vae"];
    private static readonly string[] ClipLHints = ["clip_l", "clip-l"];
    private static readonly string[] ClipGHints = ["clip_g", "clip-g"];

    private static readonly string[] AuxExtensions = [".safetensors", ".gguf", ".pt", ".ckpt", ".bin"];

    /// <summary>
    /// What the model is: read from the file where it says, and otherwise inferred from its name
    /// and from how many text encoders it carries.
    /// </summary>
    private static DiffusionKind DetectKind(string path, GgufInfo? info)
    {
        var arch = info?.Architecture?.ToLowerInvariant() ?? string.Empty;

        if (arch.Contains("flux", StringComparison.Ordinal))
        {
            return DiffusionKind.Flux;
        }

        if (arch.Contains("sd3", StringComparison.Ordinal))
        {
            return DiffusionKind.Sd3;
        }

        if (arch.Contains("xl", StringComparison.Ordinal) || info is { HasTwoTextEncoders: true })
        {
            return DiffusionKind.Sdxl;
        }

        var name = Path.GetFileName(path).ToLowerInvariant();

        if (name.Contains("flux", StringComparison.Ordinal))
        {
            return DiffusionKind.Flux;
        }

        if (name.Contains("sd3", StringComparison.Ordinal))
        {
            return DiffusionKind.Sd3;
        }

        if (SdxlNameHints.Any(hint => name.Contains(hint, StringComparison.Ordinal)))
        {
            return DiffusionKind.Sdxl;
        }

        if (Sd15NameHints.Any(hint => name.Contains(hint, StringComparison.Ordinal)))
        {
            return DiffusionKind.Sd15;
        }

        // A single text encoder is the mark of SD1.5; with nothing else to go on, the modern
        // default is the safer guess than an old one.
        if (info is { HasTextEncoder: true, HasTwoTextEncoders: false })
        {
            return DiffusionKind.Sd15;
        }

        return DiffusionKind.Unknown;
    }

    /// <summary>
    /// A companion file — a VAE or a text encoder — sitting beside the models, or null when there
    /// is none. The one that matches the model's kind is preferred over a generic match.
    /// </summary>
    private static string? FindAux(string modelPath, string[] hints, DiffusionKind kind)
    {
        try
        {
            var folder = Path.GetDirectoryName(modelPath);

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                folder = ModelsFolder;
            }

            if (!Directory.Exists(folder))
            {
                return null;
            }

            FileInfo? best = null;
            var bestScore = int.MinValue;

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var name = file.Name.ToLowerInvariant();

                if (!AuxExtensions.Contains(file.Extension.ToLowerInvariant())
                    || !hints.Any(hint => name.Contains(hint, StringComparison.Ordinal))
                    || string.Equals(file.FullName, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = 0;

                if (kind == DiffusionKind.Sdxl && (name.Contains("xl", StringComparison.Ordinal)
                                                   || name.Contains("sdxl", StringComparison.Ordinal)))
                {
                    score += 5;
                }

                if (kind == DiffusionKind.Sd15 && (name.Contains("sd15", StringComparison.Ordinal)
                                                   || name.Contains("1.5", StringComparison.Ordinal)
                                                   || name.Contains("v1-5", StringComparison.Ordinal)))
                {
                    score += 5;
                }

                // A full-precision companion is the point of supplying one, so it is preferred.
                if (name.Contains("fp16", StringComparison.Ordinal) || name.Contains("f16", StringComparison.Ordinal))
                {
                    score += 2;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = file;
                }
            }

            return best?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Generates one picture and returns its path, or the reason there is none.
    /// </summary>
    /// <remarks>
    /// Not time-boxed by a short timeout: a diffusion run on the CPU is minutes, not seconds, and
    /// the only thing that should end it early is the caller cancelling. Output and errors are read
    /// on background threads so a chatty engine cannot fill a pipe and wedge the process.
    /// </remarks>
    public static async Task<ImageGenResult> GenerateAsync(
        string prompt, ImageOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ImageGenResult(false, null, "No prompt was given, so nothing was generated.");
        }

        if (FindBinary() is not { } exe)
        {
            var where = string.Join("; ", new[]
            {
                Path.Combine(AppContext.BaseDirectory, "sd"),
                ModelsFolder,
            });

            return new ImageGenResult(false, null,
                "stable-diffusion.cpp (sd.exe) is not installed. Put sd.exe in one of: " + where + ".");
        }

        var opts = options ?? new ImageOptions();
        var model = string.IsNullOrWhiteSpace(opts.ModelPath) ? DefaultModel() : opts.ModelPath;

        if (model is null || !File.Exists(model))
        {
            return new ImageGenResult(false, null,
                "No diffusion model was found in " + ModelsFolder + ". Place a diffusion .gguf there.");
        }

        // Read the model to learn what it is and what it lacks, rather than trusting its name.
        var info = GgufInspector.Inspect(model);
        var kind = DetectKind(model, info);

        // The VAE is required outright when the model carries none of its own; even when it does,
        // a supplied fp16 VAE is used in preference, because a quantised built-in VAE is the usual
        // reason an SDXL picture comes out black.
        var vae = FindAux(model, VaeHints, kind);

        if (info is { HasVae: false } && vae is null)
        {
            return new ImageGenResult(false, null,
                "This model has no built-in VAE, so it cannot turn its output into an image. Put a "
                + "VAE file (for SDXL, sdxl_vae.safetensors) in " + ModelsFolder + ".");
        }

        // A UNet-only GGUF has to be handed the text encoders it does not include — one for SD1.5,
        // both CLIP-L and CLIP-G for SDXL.
        string? clipL = null;
        string? clipG = null;

        if (info is { HasTextEncoder: false })
        {
            clipL = FindAux(model, ClipLHints, kind);
            clipG = kind == DiffusionKind.Sdxl ? FindAux(model, ClipGHints, kind) : null;

            if (clipL is null || (kind == DiffusionKind.Sdxl && clipG is null))
            {
                return new ImageGenResult(false, null,
                    "This model has no built-in text encoder. Put clip_l"
                    + (kind == DiffusionKind.Sdxl ? " and clip_g" : string.Empty)
                    + " in " + ModelsFolder + ".");
            }
        }

        Directory.CreateDirectory(OutputFolder);
        var output = Path.Combine(OutputFolder, $"img_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // A model missing its VAE or text encoder is a standalone diffusion model, which sd loads
        // through --diffusion-model; a full checkpoint goes through -m. When the file could not be
        // read, it is treated as a full checkpoint, matching the name-based fallback.
        var standalone = info is not null && (!info.HasVae || !info.HasTextEncoder);

        foreach (var argument in BuildArguments(model, output, prompt, opts, kind, vae, clipL, clipG, standalone))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var log = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(log, e.Data);
        process.ErrorDataReceived += (_, e) => Append(log, e.Data);

        try
        {
            if (!process.Start())
            {
                return new ImageGenResult(false, null, "sd.exe could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new ImageGenResult(false, null, "sd.exe could not be started: " + ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0 || !File.Exists(output))
        {
            return new ImageGenResult(false, null,
                "sd.exe did not produce an image (exit " + process.ExitCode + "). " + Tail(log.ToString()));
        }

        return new ImageGenResult(true, output, "Generated " + Path.GetFileName(output) + ".");
    }

    private static IEnumerable<string> BuildArguments(
        string model, string output, string prompt, ImageOptions opts, DiffusionKind kind,
        string? vae, string? clipL, string? clipG, bool standalone)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        // Zeroes mean "decide from the kind": the size a model was trained at, and the guidance it
        // expects, differ enough between SD1.5, SDXL and Flux that one fixed set breaks the others.
        var size = SizeFor(kind);
        var width = opts.Width > 0 ? opts.Width : size;
        var height = opts.Height > 0 ? opts.Height : size;
        var steps = opts.Steps > 0 ? opts.Steps : 20;
        var cfg = opts.CfgScale > 0 ? opts.CfgScale : kind == DiffusionKind.Flux ? 1.0 : 7.0;

        // Passed through ArgumentList, which quotes each item itself — so a prompt with spaces or
        // quotes reaches sd as one argument and cannot spill into the command line.
        yield return standalone ? "--diffusion-model" : "-m";
        yield return model;
        yield return "-p";
        yield return prompt;

        if (!string.IsNullOrWhiteSpace(opts.Negative))
        {
            yield return "-n";
            yield return opts.Negative;
        }

        // The pieces the model does not carry itself, supplied only when detection turned one up.
        if (vae is not null)
        {
            yield return "--vae";
            yield return vae;
        }

        if (clipL is not null)
        {
            yield return "--clip_l";
            yield return clipL;
        }

        if (clipG is not null)
        {
            yield return "--clip_g";
            yield return clipG;
        }

        yield return "--cfg-scale";
        yield return cfg.ToString(culture);
        yield return "--steps";
        yield return steps.ToString(culture);
        yield return "-W";
        yield return width.ToString(culture);
        yield return "-H";
        yield return height.ToString(culture);
        yield return "--seed";
        yield return opts.Seed.ToString(culture);
        yield return "-o";
        yield return output;
    }

    /// <summary>The side a picture is generated at when the caller did not choose one.</summary>
    private static int SizeFor(DiffusionKind kind) => kind switch
    {
        DiffusionKind.Sd15 => 512,
        _ => 1024,
    };

    private static void Append(StringBuilder log, string? line)
    {
        if (line is not null)
        {
            lock (log)
            {
                log.AppendLine(line);
            }
        }
    }

    /// <summary>The last few lines of the engine's chatter — enough to say why a run failed.</summary>
    private static string Tail(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        return lines.Length <= 4 ? string.Join(" ", lines) : string.Join(" ", lines[^4..]);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
