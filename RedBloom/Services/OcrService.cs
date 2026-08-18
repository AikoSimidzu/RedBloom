using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace RedBloom.Services;

/// <summary>
/// Reads the text off a screenshot with Windows' own on-device OCR, so an agent whose model cannot
/// see pictures can still work from what is on the screen. No network, no external dependency — the
/// recogniser ships with Windows.
/// </summary>
public static class OcrService
{
    /// <summary>Whether OCR is available at all — a language pack has to be installed for it to work.</summary>
    public static bool Available => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>
    /// The text found in a PNG screenshot, laid out line by line, or an empty string when none was
    /// found or OCR is not available on this machine.
    /// </summary>
    public static async Task<string> ReadAsync(byte[] png)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? Fallback();

        if (engine is null || png.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var stream = new MemoryStream(png);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            using var bitmap = await decoder.GetSoftwareBitmapAsync();

            var result = await engine.RecognizeAsync(bitmap);

            if (result.Lines.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var line in result.Lines)
            {
                sb.AppendLine(line.Text);
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static OcrEngine? Fallback() =>
        OcrEngine.AvailableRecognizerLanguages.Count > 0
            ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
            : null;
}
