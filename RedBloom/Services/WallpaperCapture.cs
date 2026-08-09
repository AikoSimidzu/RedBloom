using System.Runtime.InteropServices;

namespace RedBloom.Services;

/// <summary>
/// Grabs the live desktop wallpaper — Wallpaper Engine's animated scenes included — and hands
/// out frames.
/// </summary>
/// <remarks>
/// Wallpaper Engine renders into a child window of the desktop, and that window yields nothing
/// on its own: PrintWindow returns black because it draws through Direct3D, and Windows
/// Graphics Capture refuses it outright because it is not a top-level window. Its ancestor,
/// Progman, does render, so the desktop as a whole is what gets captured here.
/// </remarks>
public sealed class WallpaperCapture : IDisposable
{
    private const uint PwRenderFullContent = 2;
    private const int BiRgb = 0;
    private const int DibRgbColors = 0;

    private Thread? _worker;
    private volatile bool _running;

    // Two buffers in rotation: the UI thread is still reading the previous frame while the
    // next one is being copied, and a single shared buffer would tear.
    private readonly byte[]?[] _buffers = new byte[2][];
    private int _bufferIndex;

    /// <summary>
    /// One captured desktop: top-down BGRA pixels, the width, height and stride they use, and
    /// where the desktop starts in screen pixels.
    /// </summary>
    public event Action<DesktopFrame>? FrameReady;

    public bool IsRunning => _running;

    /// <summary>Frames per second. Each frame costs real CPU, so this stays modest.</summary>
    public int FramesPerSecond { get; set; } = 10;

    /// <summary>A captured desktop, handed to the UI thread as a whole.</summary>
    /// <remarks>
    /// The full desktop is delivered rather than the slice under the window: cropping here
    /// would tie the visible region to the capture rate, so dragging the window would drag a
    /// stale background behind it. Whoever displays it picks the region instead, which costs
    /// nothing and tracks the window exactly.
    /// <para>
    /// <see cref="Pixels"/> belongs to the capture source, not to the handler: it is one of a
    /// couple of buffers in rotation and is rewritten in place a frame or two later. A handler
    /// that wants to keep the picture — to draw it on another thread, say — must copy it before
    /// returning, or it will end up drawing a half-rewritten one.
    /// </para>
    /// </remarks>
    public readonly record struct DesktopFrame(
        byte[] Pixels,
        int Width,
        int Height,
        int Stride,
        int OriginX,
        int OriginY);

    /// <summary>
    /// Identifies the current run. A stopped loop whose generation no longer matches exits for
    /// good, which a plain flag could not guarantee: stopping and starting again in quick
    /// succession — every alt-tab does exactly that — used to leave the previous thread alive,
    /// see the flag raised once more and carry on, so capture threads piled up and the machine
    /// ground to a halt.
    /// </summary>
    private int _generation;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _running = true;

        _worker = new Thread(() => Loop(generation))
        {
            IsBackground = true,
            Name = "wallpaper-capture",

            // The wallpaper is decoration; it must never compete with the terminal for CPU.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    public void Stop()
    {
        _running = false;
        Interlocked.Increment(ref _generation);
    }

    private void Loop(int generation)
    {
        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        var bits = IntPtr.Zero;
        var surfaceWidth = 0;
        var surfaceHeight = 0;

        void ReleaseSurface()
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
                dib = IntPtr.Zero;
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
                memoryDc = IntPtr.Zero;
            }

            bits = IntPtr.Zero;
            surfaceWidth = surfaceHeight = 0;
        }

        while (_running && generation == Volatile.Read(ref _generation))
        {
            var started = Environment.TickCount64;

            try
            {
                var desktop = FindWindow("Progman", null);
                if (desktop == IntPtr.Zero || !GetWindowRect(desktop, out var bounds))
                {
                    Sleep(started);
                    continue;
                }

                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;
                if (width < 2 || height < 2)
                {
                    Sleep(started);
                    continue;
                }

                if (width != surfaceWidth || height != surfaceHeight)
                {
                    ReleaseSurface();

                    var screenDc = GetDC(IntPtr.Zero);
                    memoryDc = CreateCompatibleDC(screenDc);
                    ReleaseDC(IntPtr.Zero, screenDc);

                    var header = new BitmapInfoHeader
                    {
                        Size = Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = width,

                        // Negative height gives a top-down DIB, matching how WPF reads pixels.
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = BiRgb,
                    };

                    dib = CreateDIBSection(memoryDc, ref header, DibRgbColors, out bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero)
                    {
                        ReleaseSurface();
                        Sleep(started);
                        continue;
                    }

                    previous = SelectObject(memoryDc, dib);
                    surfaceWidth = width;
                    surfaceHeight = height;
                }

                if (PrintWindow(desktop, memoryDc, PwRenderFullContent))
                {
                    EmitFrame(bits, surfaceWidth, surfaceHeight, bounds.Left, bounds.Top);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // A display change can invalidate the surface mid-capture; the next pass rebuilds it.
                ReleaseSurface();
            }

            Sleep(started);
        }

        ReleaseSurface();
    }

    private void EmitFrame(IntPtr bits, int surfaceWidth, int surfaceHeight, int originX, int originY)
    {
        if (bits == IntPtr.Zero || FrameReady is null)
        {
            return;
        }

        var stride = surfaceWidth * 4;
        var length = stride * surfaceHeight;

        _bufferIndex ^= 1;
        var buffer = _buffers[_bufferIndex];
        if (buffer is null || buffer.Length != length)
        {
            buffer = new byte[length];
            _buffers[_bufferIndex] = buffer;
        }

        Marshal.Copy(bits, buffer, 0, length);
        FrameReady.Invoke(new DesktopFrame(buffer, surfaceWidth, surfaceHeight, stride, originX, originY));
    }

    private void Sleep(long startedAt)
    {
        var budget = 1000 / Math.Clamp(FramesPerSecond, 1, 120);
        var spent = (int)(Environment.TickCount64 - startedAt);
        Thread.Sleep(Math.Max(5, budget - spent));
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfoHeader header, int usage, out IntPtr bits, IntPtr section, int offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}


