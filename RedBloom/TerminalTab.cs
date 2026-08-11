using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using RedBloom.Controls;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom;

/// <summary>
/// One entry in the tab strip. Most tabs hold a terminal, but the appearance page is a tab
/// too, so the content is any element rather than a <see cref="TerminalView"/>.
/// </summary>
public sealed class TerminalTab : INotifyPropertyChanged
{
    private string _title;
    private string _toolTip;
    private bool _isSelected;
    private bool _hasEnded;

    public TerminalTab(FrameworkElement content, string title, string glyph, string toolTip)
    {
        Content = content;
        _title = title;
        _toolTip = toolTip;
        Glyph = glyph;
    }

    public FrameworkElement Content { get; }

    /// <summary>
    /// The active terminal this tab holds, or null for pages such as the settings tab. A tab's
    /// terminal content is a <see cref="SplitContainer"/>, so this is its active pane.
    /// </summary>
    public TerminalView? View => Content switch
    {
        SplitContainer split => split.ActiveView,
        TerminalView view => view,
        _ => null,
    };

    /// <summary>The split holder for a terminal tab, or null for a page tab.</summary>
    public SplitContainer? Panes => Content as SplitContainer;

    public string Glyph { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        set => Set(ref _toolTip, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>The shell exited or the connection dropped; the scrollback is still readable.</summary>
    public bool HasEnded
    {
        get => _hasEnded;
        set => Set(ref _hasEnded, value);
    }

    // ===== per-tab card look =====

    /// <summary>The saved session behind this tab, so card edits can be persisted. Null for local tabs.</summary>
    public SshSession? Session { get; set; }

    /// <summary>The saved chat behind this tab, for the same reason. Null for everything else.</summary>
    public ChatSession? Chat { get; set; }

    /// <summary>The saved room behind this tab, when it is a group chat. Null otherwise.</summary>
    public ChatRoom? Room { get; set; }

    private TabCardStyle? _card;

    /// <summary>This tab's own card look, or null to use the theme default.</summary>
    public TabCardStyle? Card
    {
        get => _card;
        set
        {
            if (ReferenceEquals(_card, value))
            {
                return;
            }

            if (_card is not null)
            {
                _card.Changed -= OnCardChanged;
            }

            _card = value;

            if (_card is not null)
            {
                _card.Changed += OnCardChanged;
            }

            OnCardChanged();
        }
    }

    private void OnCardChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(CardBackground), nameof(CardOpacity), nameof(CardBlur),
                     nameof(CardGlyph), nameof(CardOverlayVisibility), nameof(HasCardBackground),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The card's own background — a picture or a colour — or null to keep the theme's.</summary>
    public Brush? CardBackground
    {
        get
        {
            if (_card is null)
            {
                return null;
            }

            if (_card.HasImage)
            {
                var image = LoadImageBrush(_card.ImagePath, _card.Stretch);
                if (image is not null)
                {
                    return image;
                }
            }

            return string.IsNullOrWhiteSpace(_card.Color)
                ? null
                : Frozen(new SolidColorBrush(ThemeService.ParseColor(_card.Color, Colors.Transparent)));
        }
    }

    public double CardOpacity => _card?.Opacity ?? 1.0;

    public Effect? CardBlur =>
        _card is { Blur: > 0.5 } card ? new BlurEffect { Radius = card.Blur, KernelType = KernelType.Gaussian } : null;

    /// <summary>The custom icon glyph if one is set, otherwise the tab's default.</summary>
    public string CardGlyph => string.IsNullOrWhiteSpace(_card?.Glyph) ? Glyph : _card!.Glyph;

    /// <summary>Shown only when the card actually carries a colour or picture of its own.</summary>
    public Visibility CardOverlayVisibility =>
        CardBackground is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// True when the tab draws its own background, so the default plate steps aside and the
    /// card's own translucency is what shows the picture behind the strip.
    /// </summary>
    public bool HasCardBackground => CardBackground is not null;

    private static Brush? LoadImageBrush(string path, string stretch)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return Frozen(new ImageBrush(bitmap) { Stretch = StretchOf(stretch) });
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException
                                       or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Stretch StretchOf(string name) => name switch
    {
        "Fill" => Stretch.Fill,
        "Uniform" => Stretch.Uniform,
        "None" => Stretch.None,
        _ => Stretch.UniformToFill,
    };

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
