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

    public static void Load()
    {
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
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not write {FilePath}: {ex.Message}");
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


