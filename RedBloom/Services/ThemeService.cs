using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Owns the appearance settings, persists them, and pushes them into the application's
/// resource dictionary so a change repaints the running window.
/// </summary>
public static class ThemeService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // BackgroundMode is written as a name rather than a number, so the file stays
        // legible and survives someone reordering the enum.
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RedBloom",
        "settings.json");

    /// <summary>Brush resource keys, paired with the settings property that feeds each one.</summary>
    private static readonly (string Key, Func<AppSettings, string> Value)[] BrushMap =
    [
        ("Accent", s => s.Accent),
        ("AccentDim", s => s.AccentDim),
        ("Surface", s => s.Surface),
        ("SurfaceRaised", s => s.SurfaceRaised),
        ("Chrome", s => s.Chrome),
        ("ChromeHover", s => s.ChromeHover),
        ("Divider", s => s.Divider),
        ("TextPrimary", s => s.TextPrimary),
        ("TextMuted", s => s.TextMuted),
        ("TextFaint", s => s.TextFaint),
    ];

    public static AppSettings Settings { get; } = new();

    /// <summary>Raised after the settings change and the brushes have been refreshed.</summary>
    public static event Action? Applied;

    /// <summary>
    /// Whether the file on disk has been read into <see cref="Settings"/> yet.
    /// </summary>
    /// <remarks>
    /// Guards against the one way this class can destroy data: saving before loading writes the
    /// defaults over whatever was there. The app always loads at startup, but anything else that
    /// links against it — a tool, a test — reaches a fully formed <see cref="Settings"/> holding
    /// defaults, and one call to <see cref="Save"/> would then wipe the user's agents and keys.
    /// </remarks>
    private static bool _loaded;

    public static void Load()
    {
        _loaded = true;

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (loaded is not null)
                {
                    Settings.CopyFrom(loaded);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file should not stop the app: the defaults are all valid.
            Debug.WriteLine($"Could not read {FilePath}: {ex.Message}");
        }

        Settings.WireNestedLayers();
        Apply();
        Settings.Changed += OnSettingsChanged;
    }

    public static void Save()
    {
        if (!_loaded)
        {
            // Never call this before Load: what is in memory is the defaults, and writing them
            // out would silently replace the user's settings — agents and API keys included.
            Debug.WriteLine($"Refusing to write {FilePath}: settings were never loaded.");

            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // A copy of what is being replaced, so a bad write is recoverable. The file is small
            // and rewritten often; one generation back has proved to be worth having.
            Backup();

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not write {FilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps one generation of the settings file, and only while it still holds agents.
    /// </summary>
    /// <remarks>
    /// Refusing to back up a file with no agents in it is deliberate: the loss worth recovering
    /// from is the one that empties the list, and a backup taken after that would overwrite the
    /// good copy with the damaged one.
    /// </remarks>
    private static void Backup()
    {
        try
        {
            if (!File.Exists(FilePath) || Settings.Agents.Count == 0)
            {
                return;
            }

            var existing = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(FilePath), SerializerOptions);

            if (existing is { Agents.Count: > 0 })
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not back up {FilePath}: {ex.Message}");
        }
    }

    public static void ResetToDefaults()
    {
        Settings.CopyFrom(new AppSettings());
        Apply();
        Save();
    }

    /// <summary>Parses "#RRGGBB", falling back to the supplied colour when it is not valid.</summary>
    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            var parsed = ColorConverter.ConvertFromString(hex.Trim());
            return parsed is Color color ? color : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    public static bool IsValidColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            return ColorConverter.ConvertFromString(hex.Trim()) is Color;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool _applyQueued;
    private static DispatcherTimer? _saveTimer;

    /// <summary>
    /// Dragging a slider raises this on every tick. Repainting once per frame still looks
    /// instant, and writing the file once the user pauses keeps the disk out of the loop.
    /// </summary>
    private static void OnSettingsChanged()
    {
        QueueApply();
        QueueSave();
    }

    private static void QueueApply()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Apply();
            return;
        }

        if (_applyQueued)
        {
            return;
        }

        _applyQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            // The flag is cleared even if Apply throws. Leaving it set would silently
            // suppress every later change for the rest of the session — and under a
            // debugger, where a first-chance exception can be shrugged off and execution
            // continued, that is exactly how appearance settings would appear to die.
            try
            {
                Apply();
            }
            finally
            {
                _applyQueued = false;
            }
        });
    }

    private static void QueueSave()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Save();
            return;
        }

        // Normal priority, not Background: while the user drags a slider the queue is full of
        // input, and a background tick would never get a slot — the file would go unwritten
        // for exactly as long as the user kept adjusting things.
        _saveTimer ??= new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };

        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;

        // Restarting the timer on each change means the write happens once, after the last one.
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        Save();
    }

    /// <summary>Writes any change still waiting on the debounce. Call before shutting down.</summary>
    public static void Flush()
    {
        if (_saveTimer?.IsEnabled == true)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    /// <summary>
    /// Replaces the brushes in the application resources. Every control referencing them
    /// through DynamicResource repaints; a StaticResource reference would not.
    /// </summary>
    private static void Apply()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (var (key, value) in BrushMap)
        {
            var fallback = resources[key] is SolidColorBrush existing ? existing.Color : Colors.Gray;
            var brush = new SolidColorBrush(ParseColor(value(Settings), fallback));
            brush.Freeze();
            resources[key] = brush;
        }

        resources["UiFont"] = new FontFamily(
            string.IsNullOrWhiteSpace(Settings.UiFontFamily) ? "Segoe UI" : Settings.UiFontFamily);

        // Panel fills carry their own alpha so a background picture — or the desktop — shows
        // through them by exactly the amount the user asked for.
        resources["SidebarFill"] = Translucent(Settings.Chrome, Settings.SidebarOpacity);
        resources["TabBarFill"] = Translucent(Settings.Chrome, Settings.TabBarOpacity);
        resources["TerminalFill"] = Translucent(Settings.TerminalBackground, Settings.TerminalOpacity);

        // A translucent plate under every tab, so they stay legible over a background picture
        // without hiding it. It tracks the tab bar's own see-through so the two never fight.
        resources["TabBacking"] = Translucent(Settings.SurfaceRaised, Math.Min(0.85, 0.35 + 0.5 * Settings.TabBarOpacity));

        // Same shared apply path, so a language change repaints instantly like a colour does.
        LocalizationService.Apply(Settings.Language);

        Applied?.Invoke();
    }

    private static SolidColorBrush Translucent(string hex, double opacity)
    {
        var color = ParseColor(hex, Colors.Black);
        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        brush.Freeze();
        return brush;
    }
}


