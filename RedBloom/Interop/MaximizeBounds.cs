using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RedBloom.Interop;

/// <summary>
/// Constrains a maximised window to the monitor's work area.
/// </summary>
/// <remarks>
/// A borderless WPF window maximises to the whole monitor and then some — the resize border
/// hangs off every edge — which covers the taskbar and makes the window indistinguishable from
/// borderless fullscreen. Wallpaper Engine reads that as fullscreen and pauses the wallpaper,
/// so a live background freezes the moment the terminal is maximised. Clamping to the work
/// area keeps the taskbar visible, and with it the animation.
/// </remarks>
internal static class MaximizeBounds
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>Starts clamping the window; safe to call once the handle exists.</summary>
    internal static void Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
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

    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
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

        Marshal.StructureToPtr(minMax, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
