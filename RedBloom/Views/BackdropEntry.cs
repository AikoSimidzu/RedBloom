using System.ComponentModel;
using System.Windows.Media;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>
/// Editing shim over a <see cref="BackgroundLayer"/>: gives the settings page a title, a
/// combo-friendly index for the fit mode and a live swatch for the overlay colour.
/// </summary>
public sealed class BackdropEntry : INotifyPropertyChanged
{
    public BackdropEntry(string title, BackgroundLayer layer)
    {
        Title = title;
        Layer = layer;
    }

    public string Title { get; }

    private bool _showFilePicker = true;

    /// <summary>Hidden for the live wallpaper, which has no file to choose.</summary>
    public bool ShowFilePicker
    {
        get => _showFilePicker;
        set
        {
            if (_showFilePicker != value)
            {
                _showFilePicker = value;
                OnPropertyChanged(nameof(ShowFilePicker));
            }
        }
    }

    public BackgroundLayer Layer { get; }

    public string ImagePath
    {
        get => Layer.ImagePath;
        set
        {
            Layer.ImagePath = value ?? string.Empty;
            OnPropertyChanged(nameof(ImagePath));
        }
    }

    public int StretchIndex
    {
        get => Layer.Stretch switch
        {
            "Uniform" => 1,
            "Fill" => 2,
            "None" => 3,
            _ => 0,
        };
        set
        {
            Layer.Stretch = value switch
            {
                1 => "Uniform",
                2 => "Fill",
                3 => "None",
                _ => "UniformToFill",
            };
            OnPropertyChanged(nameof(StretchIndex));
        }
    }

    public double ImageBlur
    {
        get => Layer.ImageBlur;
        set
        {
            Layer.ImageBlur = value;
            OnPropertyChanged(nameof(ImageBlur));
        }
    }

    public double ImageOpacity
    {
        get => Layer.ImageOpacity;
        set
        {
            Layer.ImageOpacity = value;
            OnPropertyChanged(nameof(ImageOpacity));
        }
    }

    /// <summary>Half-typed text, kept until it parses. Null when the box mirrors the setting.</summary>
    private string? _pendingOverlay;

    public string OverlayColor
    {
        get => _pendingOverlay ?? Layer.OverlayColor;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;

            // Half-typed colours must not reach the setting, or the window would flicker
            // through nonsense on every keystroke — but the text has to stay in the box,
            // otherwise the field overwrites itself as you type.
            _pendingOverlay = trimmed;
            if (ThemeService.IsValidColor(trimmed))
            {
                Layer.OverlayColor = trimmed;
            }

            OnPropertyChanged(nameof(OverlayColor));
            OnPropertyChanged(nameof(OverlaySwatch));
        }
    }

    public Brush OverlaySwatch
    {
        get
        {
            var brush = new SolidColorBrush(ThemeService.ParseColor(Layer.OverlayColor, Colors.Black));
            brush.Freeze();
            return brush;
        }
    }

    public double OverlayOpacity
    {
        get => Layer.OverlayOpacity;
        set
        {
            Layer.OverlayOpacity = value;
            OnPropertyChanged(nameof(OverlayOpacity));
        }
    }

    public double OverlayBlur
    {
        get => Layer.OverlayBlur;
        set
        {
            Layer.OverlayBlur = value;
            OnPropertyChanged(nameof(OverlayBlur));
        }
    }

    public void Refresh()
    {
        if (_pendingOverlay is null || ThemeService.IsValidColor(_pendingOverlay))
        {
            _pendingOverlay = null;
        }

        foreach (var name in new[]
                 {
                     nameof(ImagePath), nameof(StretchIndex), nameof(ImageBlur), nameof(ImageOpacity),
                     nameof(OverlayColor), nameof(OverlaySwatch), nameof(OverlayOpacity), nameof(OverlayBlur),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
