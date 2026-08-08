using System.ComponentModel;
using System.Reflection;
using System.Windows.Media;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>
/// One editable colour on the appearance page, bound to a property of <see cref="AppSettings"/>
/// by name so the page does not need a hand-written row per colour.
/// </summary>
public sealed class ColorEntry : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly PropertyInfo _property;

    public ColorEntry(AppSettings settings, string propertyName, string label)
    {
        _settings = settings;
        _property = typeof(AppSettings).GetProperty(propertyName)
                    ?? throw new ArgumentException($"No such setting: {propertyName}", nameof(propertyName));
        Label = label;
    }

    public string Label { get; }

    /// <summary>Half-typed text, kept until it parses. Null when the box mirrors the setting.</summary>
    private string? _pending;

    private string Stored => (string)(_property.GetValue(_settings) ?? string.Empty);

    public string Hex
    {
        get => _pending ?? Stored;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.Equals(Hex, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Typing a colour goes through invalid states like "#" and "#F"; keep the text
            // but leave the stored setting alone until it parses.
            _pending = trimmed;
            if (ThemeService.IsValidColor(trimmed))
            {
                _property.SetValue(_settings, trimmed);
            }

            OnPropertyChanged(nameof(Hex));
            OnPropertyChanged(nameof(Swatch));
            OnPropertyChanged(nameof(IsValid));
        }
    }

    public bool IsValid => ThemeService.IsValidColor(Hex);

    public Brush Swatch
    {
        get
        {
            var brush = new SolidColorBrush(ThemeService.ParseColor(Hex, Colors.Gray));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Re-reads the underlying setting, for when it changed from elsewhere.</summary>
    public void Refresh()
    {
        // Adopting the stored value would otherwise overwrite half-typed text on the very
        // next repaint, making the field impossible to type into.
        if (_pending is null || ThemeService.IsValidColor(_pending))
        {
            _pending = null;
        }

        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(Swatch));
        OnPropertyChanged(nameof(IsValid));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
