using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RedBloom.Models;

/// <summary>
/// Per-tab look for the little card in the strip: its own colour, translucency and blur, and —
/// for a saved SSH session — a background picture, how it sits, and a custom icon. Empty values
/// mean "fall back to the theme", so a tab that was never customised looks exactly as before.
/// </summary>
public sealed class TabCardStyle : INotifyPropertyChanged
{
    private string _color = string.Empty;
    private double _opacity = 1.0;
    private double _blur;
    private string _imagePath = string.Empty;
    private string _stretch = "UniformToFill";
    private string _glyph = string.Empty;

    /// <summary>Background tint as "#RRGGBB", or empty to keep the theme's tab backing.</summary>
    public string Color
    {
        get => _color;
        set => Set(ref _color, value ?? string.Empty);
    }

    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, 0, 1));
    }

    public double Blur
    {
        get => _blur;
        set => Set(ref _blur, Math.Clamp(value, 0, 60));
    }

    /// <summary>A picture drawn behind the tab card. SSH sessions only.</summary>
    public string ImagePath
    {
        get => _imagePath;
        set => Set(ref _imagePath, value ?? string.Empty);
    }

    /// <summary>Fill, Uniform, UniformToFill or None, matching WPF's Stretch.</summary>
    public string Stretch
    {
        get => _stretch;
        set => Set(ref _stretch, string.IsNullOrWhiteSpace(value) ? "UniformToFill" : value);
    }

    /// <summary>A Segoe icon glyph to show instead of the default, or empty for the default.</summary>
    public string Glyph
    {
        get => _glyph;
        set => Set(ref _glyph, value ?? string.Empty);
    }

    /// <summary>True once anything has been changed from the plain theme defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustomized =>
        !string.IsNullOrWhiteSpace(_color)
        || _opacity < 0.999
        || _blur > 0.5
        || !string.IsNullOrWhiteSpace(_imagePath)
        || !string.IsNullOrWhiteSpace(_glyph);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasImage => !string.IsNullOrWhiteSpace(_imagePath);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised for any change, so a tab can repaint itself.</summary>
    public event Action? Changed;

    public TabCardStyle Clone() => new()
    {
        Color = _color,
        Opacity = _opacity,
        Blur = _blur,
        ImagePath = _imagePath,
        Stretch = _stretch,
        Glyph = _glyph,
    };

    public void CopyFrom(TabCardStyle other)
    {
        Color = other.Color;
        Opacity = other.Opacity;
        Blur = other.Blur;
        ImagePath = other.ImagePath;
        Stretch = other.Stretch;
        Glyph = other.Glyph;
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
