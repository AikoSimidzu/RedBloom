using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RedBloom.Interop;

/// <summary>
/// Constrains a window's size: maximised to the monitor's work area, and never dragged smaller
/// than its own minimum.
/// </summary>
/// <remarks>
/// A borderless WPF window maximises to the whole monitor and then some — the resize border
/// hangs off every edge — which covers the taskbar and makes the window indistinguishable from
/// borderless fullscreen. Wallpaper Engine reads that as fullscreen and pauses the wallpaper,
/// so a live background freezes the moment the terminal is maximised. Clamping to the work
/// area keeps the taskbar visible, and with it the animation.
/// <para>
/// The minimum size rides along because both answers travel in the same message. WPF enforces
/// <see cref="FrameworkElement.MinWidth"/> and <see cref="FrameworkElement.MinHeight"/> by
/// filling <c>MinTrackSize</c> in its own reply to WM_GETMINMAXINFO — so a hook that answers
/// that message and marks it handled silently takes the minimum with it, and the window drags
/// down to nothing however the XAML is written.
/// </para>
/// </remarks>
internal static class MaximizeBounds
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>Starts clamping the window; safe to call once the handle exists.</summary>
    /// <param name="window">
    /// The window itself, read for its minimum size. Read at message time rather than captured,
    /// so a minimum changed after startup still takes effect.
    /// </param>
    internal static void Attach(IntPtr hwnd, Window window)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        HwndSource.FromHwnd(hwnd)?.AddHook(
            (IntPtr h, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                Hook(h, message, wParam, lParam, ref handled, window));
    }

    /// <summary>The work area of the monitor the window is on, in physical pixels.</summary>
    internal static (int X, int Y, int Width, int Height)? GetWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        return (info.Work.Left,
                info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top);
    }

    private static IntPtr Hook(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled, Window window)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // Positions are relative to the monitor, not the virtual desktop.
        minMax.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        minMax.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        minMax.MaxSize.X = info.Work.Right - info.Work.Left;
        minMax.MaxSize.Y = info.Work.Bottom - info.Work.Top;

        // The reply WPF would have given, had this hook not taken the message. The window's
        // minimum is in DIPs and this struct is in physical pixels, so it goes through the
        // monitor's own scaling rather than the primary display's.
        var scale = GetDpiForWindow(hwnd) is var dpi && dpi > 0 ? dpi / 96.0 : 1.0;
        minMax.MinTrackSize.X = ToPixels(window.MinWidth, scale, minMax.MinTrackSize.X);
        minMax.MinTrackSize.Y = ToPixels(window.MinHeight, scale, minMax.MinTrackSize.Y);

        Marshal.StructureToPtr(minMax, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// One dimension of the minimum in physical pixels, keeping the system's own value when the
    /// window does not ask for one (an unset minimum is zero, and an unreachable one is infinity).
    /// </summary>
    private static int ToPixels(double dips, double scale, int fallback) =>
        dips > 0 && !double.IsPositiveInfinity(dips)
            ? (int)Math.Ceiling(dips * scale)
            : fallback;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    // Per-monitor, unlike the process-wide system DPI: a window dragged to a second display with
    // different scaling reports that display's value here.
    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
