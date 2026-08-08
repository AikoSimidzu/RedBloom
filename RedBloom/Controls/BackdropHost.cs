using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Controls;

/// <summary>
/// Draws one background picture with a tinted sheet over it. Used behind the window as a
/// whole and behind individual panels, so every one of them offers the same controls.
/// </summary>
public sealed class BackdropHost : Grid
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The live wallpaper is painted as a brush rather than an Image: a brush viewbox can pick
    /// an arbitrary slice of the picture, which is what both aiming at the window and trimming
    /// the desktop icons off the edges need.
    /// </summary>
    private readonly System.Windows.Shapes.Rectangle _liveSurface = new();

    private readonly Border _overlay = new();

    private string? _loadedPath;

    public BackdropHost()
    {
        // The picture is clipped to the panel; without this a blurred edge would bleed out.
        ClipToBounds = true;
        IsHitTestVisible = false;
        Children.Add(_image);
        Children.Add(_liveSurface);
        Children.Add(_overlay);
        Visibility = Visibility.Collapsed;
    }

    private WriteableBitmap? _liveFrame;

    /// <param name="live">
    /// When set, the picture comes from <see cref="PushFrame"/> rather than from a file.
    /// </param>
    public void Apply(BackgroundLayer layer, bool active, bool live = false)
    {
        if (!active)
        {
            Visibility = Visibility.Collapsed;
            _image.Source = null;
            _loadedPath = null;
            _liveFrame = null;
            return;
        }

        if (live)
        {
            _loadedPath = null;
        }
        else
        {
            _liveFrame = null;

            if (!layer.HasImage || !LoadImage(layer.ImagePath))
            {
                Visibility = Visibility.Collapsed;
                return;
            }
        }

        Visibility = Visibility.Visible;

        _image.Stretch = layer.Stretch switch
        {
            "Fill" => Stretch.Fill,
            "Uniform" => Stretch.Uniform,
            "None" => Stretch.None,
            _ => Stretch.UniformToFill,
        };

        var blur = layer.ImageBlur > 0.5
            ? new BlurEffect { Radius = layer.ImageBlur, KernelType = KernelType.Gaussian }
            : null;

        // Only one of the two carries the picture: the Image for files, the brush-filled
        // surface for the live wallpaper, which needs a viewbox the Image cannot give.
        _image.Opacity = layer.ImageOpacity;
        _image.Effect = blur;
        _image.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        _liveSurface.Opacity = layer.ImageOpacity;
        _liveSurface.Effect = blur;
        _liveSurface.Visibility = live ? Visibility.Visible : Visibility.Collapsed;

        var tint = ThemeService.ParseColor(layer.OverlayColor, Colors.Black);
        var brush = new SolidColorBrush(tint) { Opacity = layer.OverlayOpacity };
        brush.Freeze();
        _overlay.Background = brush;
        _overlay.Effect = layer.OverlayBlur > 0.5
            ? new BlurEffect { Radius = layer.OverlayBlur, KernelType = KernelType.Gaussian }
            : null;
    }

    private double _frameDpiScale = 1;

    /// <summary>
    /// Shows one captured frame. Must be called on the UI thread; the buffer is copied into a
    /// reused bitmap rather than allocating a new one per frame.
    /// </summary>
    /// <param name="dpiScale">
    /// Device pixels per DIP. Baked into the bitmap's own DPI so an unscaled draw lands one
    /// bitmap pixel on one screen pixel.
    /// </param>
    public void PushFrame(byte[] pixels, int width, int height, int stride, double dpiScale)
    {
        if (Visibility != Visibility.Visible || width < 2 || height < 2)
        {
            return;
        }

        if (_liveFrame is null
            || _liveFrame.PixelWidth != width
            || _liveFrame.PixelHeight != height
            || Math.Abs(_frameDpiScale - dpiScale) > 0.001)
        {
            var dpi = 96 * Math.Max(0.1, dpiScale);
            _liveFrame = new WriteableBitmap(width, height, dpi, dpi, PixelFormats.Bgra32, null);
            _frameDpiScale = dpiScale;
            _image.Source = _liveFrame;
        }

        _liveFrame.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
    }

    /// <summary>
    /// Places the live frame by choosing which part of it to draw.
    /// </summary>
    /// <remarks>
    /// This is a brush viewbox, not a new capture, so dragging the window re-aims the picture
    /// immediately instead of lagging a frame behind. Aligned mode picks the slice under the
    /// window; fit mode picks the whole picture minus the requested trim.
    /// </remarks>
    public void SetLiveLayout(
        bool aligned,
        double offsetX,
        double offsetY,
        Stretch fitStretch,
        Thickness crop)
    {
        if (_liveFrame is null)
        {
            return;
        }

        var brush = EnsureLiveBrush();

        if (aligned)
        {
            // Stop panning at the edges. A window pushed past the side of the desktop has no
            // wallpaper out there, and sliding on regardless would expose bare black.
            var x = Clamp(offsetX, _liveFrame.Width, ActualWidth);
            var y = Clamp(offsetY, _liveFrame.Height, ActualHeight);

            brush.Stretch = Stretch.Fill;
            brush.Viewbox = new Rect(
                x / _liveFrame.Width,
                y / _liveFrame.Height,
                Math.Min(1, ActualWidth / _liveFrame.Width),
                Math.Min(1, ActualHeight / _liveFrame.Height));
        }
        else
        {
            var width = Math.Max(0.1, 1 - crop.Left - crop.Right);
            var height = Math.Max(0.1, 1 - crop.Top - crop.Bottom);

            brush.Stretch = fitStretch;
            brush.Viewbox = new Rect(crop.Left, crop.Top, width, height);
        }
    }

    private ImageBrush EnsureLiveBrush()
    {
        if (_liveSurface.Fill is ImageBrush existing && ReferenceEquals(existing.ImageSource, _liveFrame))
        {
            return existing;
        }

        var brush = new ImageBrush(_liveFrame)
        {
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };

        _liveSurface.Fill = brush;
        return brush;
    }

    /// <summary>Keeps the offset within the range that still covers the panel.</summary>
    private static double Clamp(double offset, double imageSize, double hostSize)
    {
        var slack = imageSize - hostSize;
        return slack <= 0 ? 0 : Math.Clamp(offset, 0, slack);
    }

    private bool LoadImage(string path)
    {
        if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) && _image.Source is not null)
        {
            return true;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);

            // Loaded up front so the file is not held open, which would stop the user
            // replacing or deleting the picture while RedBloom is running.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            _image.Source = bitmap;
            _loadedPath = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException
                                       or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not load background {path}: {ex.Message}");
            _image.Source = null;
            _loadedPath = null;
            return false;
        }
    }
}

