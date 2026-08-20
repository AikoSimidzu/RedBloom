using System.IO;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Finds the extensions the app can run and remembers which are on. Two places are scanned: the ones
/// that ship with the app (under <c>Assets/Extensions</c>) and the ones the user drops into their
/// profile (<c>%APPDATA%/RedBloom/extensions</c>). Each is a folder with a <c>manifest.json</c>.
/// </summary>
public static class ExtensionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The built-in extensions that ship next to the app.</summary>
    private static string BuiltInRoot => Path.Combine(AppContext.BaseDirectory, "Assets", "Extensions");

    /// <summary>Where the user's own extensions live.</summary>
    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "extensions");

    private static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "extensions.json");

    /// <summary>A discovered extension: its manifest and the folders it lives and keeps data in.</summary>
    public sealed record Extension(ExtensionManifest Manifest, string Root, string EntryPath, string DataDir, bool BuiltIn)
    {
        public string Id => Manifest.Id;

        /// <summary>Whether the user has this extension turned on.</summary>
        public bool Enabled { get; set; }
    }

    /// <summary>Every extension found, built-ins first, each carrying its on/off state.</summary>
    public static List<Extension> All()
    {
        var found = new List<Extension>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = LoadState();

        foreach (var (root, builtIn) in new[] { (BuiltInRoot, true), (UserRoot, false) })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (Load(dir, builtIn) is { } ext && seen.Add(ext.Id))
                {
                    // Built-ins are on by default; a user extension stays off until turned on, so a
                    // folder dropped in cannot start running programs unasked.
                    ext.Enabled = state.TryGetValue(ext.Id, out var on) ? on : builtIn;
                    found.Add(ext);
                }
            }
        }

        return found;
    }

    /// <summary>The extension with this id, or null.</summary>
    public static Extension? ById(string id) => All().FirstOrDefault(e => e.Id == id);

    /// <summary>Turns an extension on or off and remembers the choice.</summary>
    public static void SetEnabled(string id, bool on)
    {
        var state = LoadState();
        state[id] = on;
        SaveState(state);
    }

    private static Extension? Load(string dir, bool builtIn)
    {
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(manifestPath), Json) is not { } manifest
                || string.IsNullOrWhiteSpace(manifest.Id))
            {
                return null;
            }

            var entry = Path.Combine(dir, manifest.Entry);
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "extensions-data", manifest.Id);

            return new Extension(manifest, dir, entry, dataDir, builtIn);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load extension at {dir}: {ex.Message}");
            return null;
        }
    }

    // ---- program resolution ----

    /// <summary>
    /// Turns a program name an extension declared (e.g. "arduino-cli") into a full path to run, or
    /// null when it is not installed. PATH is searched, plus a few well-known install locations.
    /// </summary>
    public static string? ResolveProgram(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
        {
            return null;
        }

        foreach (var candidate in Candidates(name))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; keep looking.
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var exts = new[] { ".exe", ".cmd", ".bat", "" };

        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            foreach (var folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in exts)
                {
                    yield return Path.Combine(folder.Trim('"'), name + ext);
                }
            }
        }

        // Well-known spots for the tools the built-in extensions use.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Arduino CLI", name + ".exe");
    }

    // ---- enabled state ----

    private static Dictionary<string, bool> LoadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(StateFile), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void SaveState(Dictionary<string, bool> state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFile);
            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save extension state: {ex.Message}");
        }
    }
}
