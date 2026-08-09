using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RedBloom.Services;

namespace RedBloom.Controls;

/// <summary>
/// A small, reusable colour picker that drops down from any swatch. It edits by hue plus a
/// saturation/value square — the same model every graphics tool uses — and echoes the result
/// as "#RRGGBB" so it fits the hex fields the rest of the app already stores.
/// </summary>
/// <remarks>
/// One instance is reused for the whole app: only one picker is ever open at a time, and making
/// a fresh visual tree per click would churn brushes for nothing. Callers hand it a getter's
/// value and a setter callback, so it stays ignorant of what it is editing — a terminal colour,
/// an overlay tint, or a tab card.
/// </remarks>
public sealed class ColorPickerPopup : Popup
{
    private const double SvWidth = 208;
    private const double SvHeight = 150;
    private const double HueWidth = 208;
    private const double HueHeight = 16;

    private static ColorPickerPopup? _shared;

    /// <summary>Opens the shared picker under <paramref name="anchor"/>, seeded from a hex string.</summary>
    /// <param name="onClosed">
    /// Runs when the picker closes. Lets a caller that lives inside its own auto-closing popup
    /// hold that popup open while the picker is up and let it go again afterwards.
    /// </param>
    public static void Show(UIElement anchor, string? initialHex, Action<string> onChange, Action? onClosed = null)
    {
        _shared ??= new ColorPickerPopup();
        _shared.Open(anchor, initialHex, onChange, onClosed);
    }

    private Action? _onClosed;

    // Current colour in HSV, which is what the two controls edit directly. RGB is derived.
    private double _hue;          // 0..360
    private double _saturation;   // 0..1
    private double _value;        // 0..1

    private Action<string>? _onChange;
    private bool _updating;

    private readonly Border _svArea;
    private readonly Rectangle _hueFill;
    private readonly Ellipse _svThumb;
    private readonly Border _hueThumb;
    private readonly Border _preview;
    private readonly TextBox _hexBox;

    private ColorPickerPopup()
    {
        StaysOpen = false;
        AllowsTransparency = true;
        PopupAnimation = PopupAnimation.Fade;
        Placement = PlacementMode.Bottom;
        VerticalOffset = 4;

        // --- saturation / value square ---
        _hueFill = new Rectangle { Width = SvWidth, Height = SvHeight };

        // White across (saturation) then black down (value), painted over the pure hue below.
        var whiteOverlay = new Rectangle
        {
            Width = SvWidth,
            Height = SvHeight,
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var blackOverlay = new Rectangle
        {
            Width = SvWidth,
            Height = SvHeight,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        };

        _svThumb = new Ellipse
        {
            Width = 13,
            Height = 13,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.7,
                Color = Colors.Black,
            },
        };

        var svCanvas = new Canvas { Width = SvWidth, Height = SvHeight, ClipToBounds = true };
        svCanvas.Children.Add(_hueFill);
        svCanvas.Children.Add(whiteOverlay);
        svCanvas.Children.Add(blackOverlay);
        svCanvas.Children.Add(_svThumb);

        _svArea = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = svCanvas,
            Cursor = Cursors.Cross,
        };
        _svArea.MouseLeftButtonDown += SvArea_Mouse;
        _svArea.MouseMove += SvArea_Mouse;
        _svArea.MouseLeftButtonUp += (_, _) => _svArea.ReleaseMouseCapture();

        // --- hue strip ---
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var stop = 0; stop <= 6; stop++)
        {
            rainbow.GradientStops.Add(new GradientStop(FromHsv(stop * 60, 1, 1), stop / 6.0));
        }

        var hueStrip = new Rectangle { Width = HueWidth, Height = HueHeight, Fill = rainbow };
        _hueThumb = new Border
        {
            Width = 6,
            Height = HueHeight + 6,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.7,
                Color = Colors.Black,
            },
        };
        Canvas.SetTop(_hueThumb, -3);

        var hueCanvas = new Canvas { Width = HueWidth, Height = HueHeight };
        hueCanvas.Children.Add(hueStrip);
        hueCanvas.Children.Add(_hueThumb);

        var hueBorder = new Border
        {
            CornerRadius = new CornerRadius(5),
            ClipToBounds = false,
            Child = hueCanvas,
            Margin = new Thickness(0, 12, 0, 0),
            Cursor = Cursors.Cross,
        };
        hueBorder.MouseLeftButtonDown += Hue_Mouse;
        hueBorder.MouseMove += Hue_Mouse;
        hueBorder.MouseLeftButtonUp += (_, _) => ((Border)hueBorder).ReleaseMouseCapture();

        // --- hex field with a live preview ---
        _preview = new Border
        {
            Width = 30,
            Height = 26,
            CornerRadius = new CornerRadius(5),
            BorderBrush = ThemeBrush("Divider"),
            BorderThickness = new Thickness(1),
        };
        _hexBox = new TextBox
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11.5,
            Padding = new Thickness(7, 4, 7, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _hexBox.TextChanged += Hex_TextChanged;

        var hexRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_preview, 0);
        Grid.SetColumn(_hexBox, 2);
        hexRow.Children.Add(_preview);
        hexRow.Children.Add(_hexBox);

        var panel = new StackPanel();
        panel.Children.Add(_svArea);
        panel.Children.Add(hueBorder);
        panel.Children.Add(hexRow);

        var frame = new Border
        {
            Background = ThemeBrush("Chrome"),
            BorderBrush = ThemeBrush("Divider"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.5,
                Color = Colors.Black,
            },
        };

        Child = frame;

        Closed += (_, _) =>
        {
            var closed = _onClosed;
            _onClosed = null;
            closed?.Invoke();
        };
    }

    private void Open(UIElement anchor, string? initialHex, Action<string> onChange, Action? onClosed)
    {
        _onChange = null; // don't fire the previous target while seeding this one
        _onClosed = onClosed;
        PlacementTarget = anchor;

        var color = ThemeService.ParseColor(initialHex, Colors.Gray);
        (_hue, _saturation, _value) = ToHsv(color);

        _updating = true;
        _hexBox.Text = ToHex(color);
        _updating = false;

        RedrawAll();
        _onChange = onChange;
        IsOpen = true;
    }

    // ---- editing ----

    private void SvArea_Mouse(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _svArea.CaptureMouse();
        var p = e.GetPosition(_svArea);
        _saturation = Math.Clamp(p.X / SvWidth, 0, 1);
        _value = Math.Clamp(1 - p.Y / SvHeight, 0, 1);
        Commit();
    }

    private void Hue_Mouse(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var border = (Border)sender;
        border.CaptureMouse();
        var p = e.GetPosition(border);
        _hue = Math.Clamp(p.X / HueWidth, 0, 1) * 360;
        Commit();
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !ThemeService.IsValidColor(_hexBox.Text))
        {
            return;
        }

        var color = ThemeService.ParseColor(_hexBox.Text, Colors.Gray);
        (_hue, _saturation, _value) = ToHsv(color);
        RedrawThumbs();
        _hueFill.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
        _preview.Background = new SolidColorBrush(color);
        _onChange?.Invoke(ToHex(color));
    }

    /// <summary>Applies an edit made on the square or strip: hex text follows the colour.</summary>
    private void Commit()
    {
        var color = FromHsv(_hue, _saturation, _value);
        var hex = ToHex(color);

        _updating = true;
        _hexBox.Text = hex;
        _updating = false;

        RedrawAll();
        _onChange?.Invoke(hex);
    }

    private void RedrawAll()
    {
        _hueFill.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
        _preview.Background = new SolidColorBrush(FromHsv(_hue, _saturation, _value));
        RedrawThumbs();
    }

    private void RedrawThumbs()
    {
        Canvas.SetLeft(_svThumb, _saturation * SvWidth - _svThumb.Width / 2);
        Canvas.SetTop(_svThumb, (1 - _value) * SvHeight - _svThumb.Height / 2);
        Canvas.SetLeft(_hueThumb, _hue / 360 * HueWidth - _hueThumb.Width / 2);
    }

    // ---- helpers ----

    private static SolidColorBrush ThemeBrush(string key) =>
        Application.Current.TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(Colors.Gray);

    private static string ToHex(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0.00001)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        var saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = value - c;

        double r, g, b;
        switch ((int)(hue / 60) % 6)
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
