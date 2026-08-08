using System.Runtime.InteropServices;

namespace RedBloom.Interop;

/// <summary>Desktop Window Manager attributes used to shape the window frame.</summary>
internal static class Dwm
{
    private const int DwmwaWindowCornerPreference = 33;

    internal enum CornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;

        /// <summary>Tint in AABBGGRR order, not the usual ARGB.</summary>
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int DwmwaSystemBackdropType = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// Turns on the Windows 11 system backdrop, which needs the frame extended over the whole
    /// client area first — otherwise the window surface stays opaque and there is nothing for
    /// DWM to show through.
    /// </summary>
    /// <returns><c>true</c> if the platform accepted the request.</returns>
    internal static bool TrySetSystemBackdrop(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var margins = enabled
                ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : default;
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // 3 is the transient acrylic backdrop; 1 asks DWM to pick none.
            var backdrop = enabled ? 3 : 1;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts a blurred view of whatever is behind the window under its content, tinted by
    /// <paramref name="tint"/>. Panels that are themselves translucent then show it through.
    /// </summary>
    /// <remarks>
    /// Per-pixel window alpha via WS_EX_LAYERED is not an option here: the call succeeds but
    /// the style never takes, because the window is a DirectComposition target — both WPF and
    /// the WebView2 control make it one, and DirectComposition and layered windows are
    /// mutually exclusive. The composition accent policy works on such windows.
    /// </remarks>
    internal static void SetAcrylicBackdrop(IntPtr hwnd, System.Windows.Media.Color tint, double strength)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = strength > 0.001;
        var alpha = (byte)Math.Clamp(Math.Round((1.0 - strength) * 255), 0, 255);

        var policy = new AccentPolicy
        {
            AccentState = enabled ? AccentEnableAcrylicBlurBehind : AccentDisabled,
            AccentFlags = 2,
            GradientColor = (uint)((alpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R),
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            // Not available on this build of Windows; the window simply stays opaque.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Rounds the window frame. DWM owns the corners on Windows 11 — a CornerRadius on
    /// WindowChrome does nothing for a WindowStyle="None" window. Silently does nothing on
    /// Windows 10, which has no such attribute.
    /// </summary>
    internal static void SetCornerPreference(IntPtr hwnd, CornerPreference preference)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = (int)preference;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-Windows-11 host; square corners are the only option.
        }
    }
}

