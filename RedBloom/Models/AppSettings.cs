using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RedBloom.Models;

/// <summary>
/// Everything the appearance page can change. Colours are kept as "#RRGGBB" strings so the
/// settings file stays readable and hand-editable.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    // ---- interface ----
    private AppLanguage _language = AppLanguage.English;

    /// <summary>Language of the interface. Applied live, like every other appearance setting.</summary>
    public AppLanguage Language { get => _language; set => Set(ref _language, value); }

    // ---- application chrome ----
    private string _accent = "#FF4D5A";
    private string _accentDim = "#8C2B33";
    private string _surface = "#171616";
    private string _surfaceRaised = "#1E1C1C";
    private string _chrome = "#221F1F";
    private string _chromeHover = "#332E2E";
    private string _divider = "#2D2D2D";
    private string _textPrimary = "#E6E0E0";
    private string _textMuted = "#A89F9F";
    private string _textFaint = "#6B6363";
    private string _uiFontFamily = "Segoe UI";

    // ---- terminal ----
    private string _terminalFontFamily = "Cascadia Mono";
    private double _terminalFontSize = 14;
    private double _terminalLineHeight = 1.2;
    private string _cursorStyle = "bar";
    private bool _cursorBlink = true;
    private int _scrollback = 10000;

    private string _terminalBackground = "#171616";
    private string _terminalForeground = "#E6E0E0";
    private string _terminalCursor = "#FF4D5A";
    private string _terminalSelection = "#5A2A2F";

    // ---- ANSI palette ----
    private string _ansiBlack = "#2D2D2D";
    private string _ansiRed = "#FF4D5A";
    private string _ansiGreen = "#5FD68A";
    private string _ansiYellow = "#F0C674";
    private string _ansiBlue = "#6AA9F0";
    private string _ansiMagenta = "#C78BF0";
    private string _ansiCyan = "#5FD0D6";
    private string _ansiWhite = "#D6CFCF";
    private string _ansiBrightBlack = "#6B6363";
    private string _ansiBrightRed = "#FF7B85";
    private string _ansiBrightGreen = "#8CE8AC";
    private string _ansiBrightYellow = "#FFDB94";
    private string _ansiBrightBlue = "#96C5FF";
    private string _ansiBrightMagenta = "#DBB0FF";
    private string _ansiBrightCyan = "#8EE9EE";
    private string _ansiBrightWhite = "#FFFFFF";

    public string Accent { get => _accent; set => Set(ref _accent, value); }
    public string AccentDim { get => _accentDim; set => Set(ref _accentDim, value); }
    public string Surface { get => _surface; set => Set(ref _surface, value); }
    public string SurfaceRaised { get => _surfaceRaised; set => Set(ref _surfaceRaised, value); }
    public string Chrome { get => _chrome; set => Set(ref _chrome, value); }
    public string ChromeHover { get => _chromeHover; set => Set(ref _chromeHover, value); }
    public string Divider { get => _divider; set => Set(ref _divider, value); }
    public string TextPrimary { get => _textPrimary; set => Set(ref _textPrimary, value); }
    public string TextMuted { get => _textMuted; set => Set(ref _textMuted, value); }
    public string TextFaint { get => _textFaint; set => Set(ref _textFaint, value); }
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }

    public string TerminalFontFamily { get => _terminalFontFamily; set => Set(ref _terminalFontFamily, value); }
    public double TerminalFontSize { get => _terminalFontSize; set => Set(ref _terminalFontSize, Math.Clamp(value, 6, 40)); }
    public double TerminalLineHeight { get => _terminalLineHeight; set => Set(ref _terminalLineHeight, Math.Clamp(value, 0.8, 2.5)); }
    public string CursorStyle { get => _cursorStyle; set => Set(ref _cursorStyle, value); }
    public bool CursorBlink { get => _cursorBlink; set => Set(ref _cursorBlink, value); }
    public int Scrollback { get => _scrollback; set => Set(ref _scrollback, Math.Clamp(value, 100, 200000)); }

    public string TerminalBackground { get => _terminalBackground; set => Set(ref _terminalBackground, value); }
    public string TerminalForeground { get => _terminalForeground; set => Set(ref _terminalForeground, value); }
    public string TerminalCursor { get => _terminalCursor; set => Set(ref _terminalCursor, value); }
    public string TerminalSelection { get => _terminalSelection; set => Set(ref _terminalSelection, value); }

    public string AnsiBlack { get => _ansiBlack; set => Set(ref _ansiBlack, value); }
    public string AnsiRed { get => _ansiRed; set => Set(ref _ansiRed, value); }
    public string AnsiGreen { get => _ansiGreen; set => Set(ref _ansiGreen, value); }
    public string AnsiYellow { get => _ansiYellow; set => Set(ref _ansiYellow, value); }
    public string AnsiBlue { get => _ansiBlue; set => Set(ref _ansiBlue, value); }
    public string AnsiMagenta { get => _ansiMagenta; set => Set(ref _ansiMagenta, value); }
    public string AnsiCyan { get => _ansiCyan; set => Set(ref _ansiCyan, value); }
    public string AnsiWhite { get => _ansiWhite; set => Set(ref _ansiWhite, value); }
    public string AnsiBrightBlack { get => _ansiBrightBlack; set => Set(ref _ansiBrightBlack, value); }
    public string AnsiBrightRed { get => _ansiBrightRed; set => Set(ref _ansiBrightRed, value); }
    public string AnsiBrightGreen { get => _ansiBrightGreen; set => Set(ref _ansiBrightGreen, value); }
    public string AnsiBrightYellow { get => _ansiBrightYellow; set => Set(ref _ansiBrightYellow, value); }
    public string AnsiBrightBlue { get => _ansiBrightBlue; set => Set(ref _ansiBrightBlue, value); }
    public string AnsiBrightMagenta { get => _ansiBrightMagenta; set => Set(ref _ansiBrightMagenta, value); }
    public string AnsiBrightCyan { get => _ansiBrightCyan; set => Set(ref _ansiBrightCyan, value); }
    public string AnsiBrightWhite { get => _ansiBrightWhite; set => Set(ref _ansiBrightWhite, value); }

    // ---- background pictures ----
    private BackgroundMode _backgroundMode = BackgroundMode.None;

    /// <summary>Where background pictures are drawn: nowhere, one behind the window, or one per panel.</summary>
    public BackgroundMode BackgroundMode
    {
        get => _backgroundMode;
        set => Set(ref _backgroundMode, value);
    }

    /// <summary>Used when <see cref="BackgroundMode"/> is Window.</summary>
    public BackgroundLayer WindowBackdrop { get; init; } = new();

    /// <summary>Used when <see cref="BackgroundMode"/> is Regions.</summary>
    public BackgroundLayer SidebarBackdrop { get; init; } = new();

    public BackgroundLayer TerminalBackdrop { get; init; } = new();

    // ---- live wallpaper ----
    private int _wallpaperFps = 10;
    private bool _alwaysOnTop;
    private WallpaperDisplay _wallpaperDisplay = WallpaperDisplay.AlignedToDesktop;

    /// <summary>Whether the live wallpaper follows the desktop or fills the window.</summary>
    public WallpaperDisplay WallpaperDisplay
    {
        get => _wallpaperDisplay;
        set => Set(ref _wallpaperDisplay, value);
    }

    /// <summary>
    /// How often the live wallpaper is re-grabbed. Every frame costs CPU, so this is worth
    /// keeping low unless the wallpaper actually moves quickly.
    /// </summary>
    public int WallpaperFps
    {
        get => _wallpaperFps;
        set => Set(ref _wallpaperFps, Math.Clamp(value, 1, 120));
    }

    /// <summary>Keeps the window above others. Off by default — it is intrusive.</summary>
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => Set(ref _alwaysOnTop, value);
    }

    private double _wallpaperCropLeft;
    private double _wallpaperCropTop;
    private double _wallpaperCropRight;
    private double _wallpaperCropBottom;

    // Desktop icons are drawn into the same surface as the wallpaper and cannot be separated
    // from it, so trimming the edges is what keeps them out of the picture.
    public double WallpaperCropLeft
    {
        get => _wallpaperCropLeft;
        set => Set(ref _wallpaperCropLeft, Math.Clamp(value, 0, 0.45));
    }

    public double WallpaperCropTop
    {
        get => _wallpaperCropTop;
        set => Set(ref _wallpaperCropTop, Math.Clamp(value, 0, 0.45));
    }

    public double WallpaperCropRight
    {
        get => _wallpaperCropRight;
        set => Set(ref _wallpaperCropRight, Math.Clamp(value, 0, 0.45));
    }

    public double WallpaperCropBottom
    {
        get => _wallpaperCropBottom;
        set => Set(ref _wallpaperCropBottom, Math.Clamp(value, 0, 0.45));
    }

    // ---- see-through ----
    private double _windowOpacity = 1.0;
    private double _sidebarOpacity = 1.0;
    private double _tabBarOpacity = 1.0;
    private double _terminalOpacity = 1.0;

    /// <summary>Alpha of the whole window, desktop included. 1 is fully solid.</summary>
    public double WindowOpacity
    {
        get => _windowOpacity;
        set => Set(ref _windowOpacity, Math.Clamp(value, 0.2, 1));
    }

    /// <summary>How solid the sidebar's own colour is over whatever is behind it.</summary>
    public double SidebarOpacity
    {
        get => _sidebarOpacity;
        set => Set(ref _sidebarOpacity, Math.Clamp(value, 0, 1));
    }

    public double TabBarOpacity
    {
        get => _tabBarOpacity;
        set => Set(ref _tabBarOpacity, Math.Clamp(value, 0, 1));
    }

    public double TerminalOpacity
    {
        get => _terminalOpacity;
        set => Set(ref _terminalOpacity, Math.Clamp(value, 0, 1));
    }

    /// <summary>Raised for any change, so listeners can re-apply without naming every property.</summary>
    // ---- AI agents ----

    /// <summary>
    /// The configured agents, in the order the AI settings page lists them.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale on load rather than merged, so the reflection copy in
    /// <see cref="CopyFrom"/> handles it without help. Edits inside an agent do not raise
    /// <see cref="Changed"/> — the page that makes them saves explicitly, which keeps a
    /// half-typed endpoint from being written on every keystroke.
    /// </remarks>
    public List<AiAgent> Agents { get; set; } = [];

    /// <summary>
    /// Names given to models found on this machine, by model id.
    /// </summary>
    /// <remarks>
    /// Only the name is kept, not the model. Local models are discovered afresh each time the
    /// list is shown — see <see cref="Services.Ai.LocalAgents"/> — so storing the whole thing
    /// would create entries that outlive the files. A name, on the other hand, is the one part
    /// the machine cannot work out for itself, and an entry left behind after a model is deleted
    /// costs a line of text.
    /// </remarks>
    public Dictionary<string, string> LocalModelNames { get; set; } = [];

    public event Action? Changed;

    /// <summary>Subscribes to the nested layers so their edits raise <see cref="Changed"/> too.</summary>
    public void WireNestedLayers()
    {
        foreach (var layer in new[] { WindowBackdrop, SidebarBackdrop, TerminalBackdrop })
        {
            layer.Changed -= OnLayerChanged;
            layer.Changed += OnLayerChanged;
        }
    }

    private void OnLayerChanged() => Changed?.Invoke();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            // The nested layers are init-only and copied field by field below, so that
            // subscribers keep pointing at the same instances.
            if (property is { CanRead: true, CanWrite: true }
                && property.PropertyType != typeof(BackgroundLayer))
            {
                property.SetValue(this, property.GetValue(other));
            }
        }

        WindowBackdrop.CopyFrom(other.WindowBackdrop);
        SidebarBackdrop.CopyFrom(other.SidebarBackdrop);
        TerminalBackdrop.CopyFrom(other.TerminalBackdrop);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        Changed?.Invoke();
    }
}


