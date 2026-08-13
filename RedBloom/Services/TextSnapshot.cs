using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RedBloom.Services;

/// <summary>
/// Renders a piece of selected text to an image and puts it on the clipboard.
/// </summary>
/// <remarks>
/// For sharing a snippet as a picture rather than as plain text — a reply pasted into a chat that
/// does not keep formatting, say. Drawn on a rounded plate the colour of the app's dividers, in
/// white Segoe UI, at twice the pixel density so it stays crisp where it lands.
/// </remarks>
public static class TextSnapshot
{
    private const double Padding = 18;
    private const double MaxWidth = 760;
    private const double Radius = 12;
    private const double FontSize = 15;
    private const double Scale = 2;

    /// <summary>Draws the text and copies the picture. Silently does nothing for empty text.</summary>
    public static void CopyToClipboard(string text)
    {
        text = text.Replace("\r\n", "\n").TrimEnd();

        if (text.Length == 0)
        {
            return;
        }

        var typeface = new Typeface(
            new FontFamily("Segoe UI, Segoe UI Variable Text"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Brushes.White,
            pixelsPerDip: 1.0)
        {
            MaxTextWidth = MaxWidth - (Padding * 2),
            LineHeight = FontSize * 1.45,
        };

        var width = Math.Ceiling(formatted.Width + (Padding * 2));
        var height = Math.Ceiling(formatted.Height + (Padding * 2));

        var plate = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
        plate.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(plate, null, new Rect(0, 0, width, height), Radius, Radius);
            dc.DrawText(formatted, new Point(Padding, Padding));
        }

        var bitmap = new RenderTargetBitmap(
            (int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        try
        {
            Clipboard.SetImage(bitmap);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is briefly held by another app now and then; nothing to do but leave it.
        }
    }
}
