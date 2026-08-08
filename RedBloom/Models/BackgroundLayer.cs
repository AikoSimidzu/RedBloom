using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RedBloom.Models;

/// <summary>Where a background picture is drawn.</summary>
public enum BackgroundMode
{
    /// <summary>No picture at all.</summary>
    None,

    /// <summary>One picture behind the whole window, continuous across every panel.</summary>
    Window,

    /// <summary>A picture per panel, so the sidebar and the terminal can differ.</summary>
    Regions,

    /// <summary>
    /// The live desktop wallpaper, animation and all. Uses the window layer's blur, overlay
    /// and opacity settings; the picture file is ignored.
    /// </summary>
    LiveWallpaper,
}

/// <summary>How the live wallpaper is laid out inside the window.</summary>
public enum WallpaperDisplay
{
    /// <summary>
    /// The wallpaper stays put on the desktop and the window shows the part it covers, as
    /// though it were see-through. Moving the window slides the picture underneath it.
    /// </summary>
    AlignedToDesktop,

    /// <summary>
    /// The whole wallpaper is drawn inside the window, scaled to fit. Moving the window
    /// changes nothing about the picture.
    /// </summary>
    FitToWindow,
}

/// <summary>
/// A picture plus the tinted sheet drawn over it. Used for the window-wide backdrop and for
/// each panel, so all of them offer the same controls.
/// </summary>
public sealed class BackgroundLayer : INotifyPropertyChanged
{
    private string _imagePath = string.Empty;
    private string _stretch = "UniformToFill";
    private double _imageBlur;
    private double _imageOpacity = 1.0;
    private string _overlayColor = "#000000";
    private double _overlayOpacity = 0.35;
    private double _overlayBlur;

    public string ImagePath
    {
        get => _imagePath;
        set => Set(ref _imagePath, value);
    }

    /// <summary>Fill, Uniform, UniformToFill or None, matching WPF's Stretch.</summary>
    public string Stretch
    {
        get => _stretch;
        set => Set(ref _stretch, value);
    }

    /// <summary>Blur radius applied to the picture itself, in device-independent pixels.</summary>
    public double ImageBlur
    {
        get => _imageBlur;
        set => Set(ref _imageBlur, Math.Clamp(value, 0, 60));
    }

    public double ImageOpacity
    {
        get => _imageOpacity;
        set => Set(ref _imageOpacity, Math.Clamp(value, 0, 1));
    }

    /// <summary>Colour of the sheet laid over the picture, usually to calm it down behind text.</summary>
    public string OverlayColor
    {
        get => _overlayColor;
        set => Set(ref _overlayColor, value);
    }

    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set => Set(ref _overlayOpacity, Math.Clamp(value, 0, 1));
    }

    public double OverlayBlur
    {
        get => _overlayBlur;
        set => Set(ref _overlayBlur, Math.Clamp(value, 0, 60));
    }

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised for any change, so the owning settings object can forward it.</summary>
    public event Action? Changed;

    public void CopyFrom(BackgroundLayer other)
    {
        ImagePath = other.ImagePath;
        Stretch = other.Stretch;
        ImageBlur = other.ImageBlur;
        ImageOpacity = other.ImageOpacity;
        OverlayColor = other.OverlayColor;
        OverlayOpacity = other.OverlayOpacity;
        OverlayBlur = other.OverlayBlur;
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
