using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace RedBloom.Services;

/// <summary>
/// Grabs the animated wallpaper straight out of Wallpaper Engine, free of the desktop icons.
/// </summary>
/// <remarks>
/// The desktop icons (SHELLDLL_DefView) and the wallpaper (WorkerW, with Wallpaper Engine's
/// DX11 window inside it) are sibling child windows of Progman, and every Windows capture API
/// works at top-level-window granularity or coarser, so all of them return the two composed
/// together. The only place the wallpaper exists on its own is inside Wallpaper Engine's swap
/// chain, before the desktop is composed. So a native hook (Native\RedBloomHook.dll) is
/// injected into that process; it copies each presented frame into shared memory, and this
/// class reads it out. It is the same mechanism OBS uses for game capture.
/// </remarks>
public sealed class WallpaperEngineCapture : IDisposable
{
    private const string MapName = @"Local\RedBloomWallpaperFrame";
    private const uint Magic = 0x50574252; // "RBWP"
    private const string EngineProcessName = "wallpaper64";

    // Must match Shared.h. The header carries the layout; two rotating buffers follow it.
    private const int HeaderBytes = 4096;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1200;
    private const int BufferBytes = MaxWidth * MaxHeight * 4;
    private const int BufferCount = 2;

    // Header field offsets, in the order they are declared, with the two 64-bit fields on
    // 8-byte boundaries exactly as the C++ compiler lays them out.
    private const int OffMagic = 0;
    private const int OffWidth = 4;
    private const int OffHeight = 8;
    private const int OffStride = 12;
    private const int OffChannels = 16;
    private const int OffLatest = 20;
    private const int OffFrameIndex = 24;
    private const int OffIntervalMs = 32;
    private const int OffReaderTickMs = 40;

    private Thread? _worker;
    private volatile bool _running;
    private int _generation;
    private long _lastFrameIndex = -1;
    private long _lastFrameAt;
    private bool _stallReported;

    // Two managed buffers in rotation, so the UI thread can still be reading the previous
    // frame while the next is copied out of shared memory.
    private readonly byte[]?[] _buffers = new byte[BufferCount][];
    private int _bufferIndex;

    public event Action<WallpaperCapture.DesktopFrame>? FrameReady;

    /// <summary>
    /// Raised when this source gives up on the run it was asked for: the hook could not be
    /// injected, or the block it publishes into went away with a restarted Wallpaper Engine.
    /// </summary>
    /// <remarks>
    /// Injection happens on the capture thread and takes a moment, so whether it worked is not
    /// known when <see cref="Start"/> returns — this is how the answer comes back. It is raised
    /// on the capture thread, at most once per run, and never for an ordinary <see cref="Stop"/>.
    /// </remarks>
    public event Action? Unavailable;

    /// <summary>
    /// Raised when the hook is in place and healthy but no new frame has arrived for a while,
    /// which means Wallpaper Engine has stopped drawing.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud, because it is the one failure that looks exactly like a bug in
    /// this app and is not one: Wallpaper Engine pauses itself when a window is maximised or
    /// fullscreen — that is its default — and a paused engine presents nothing to capture. Left
    /// unexplained, the background simply stops moving and the reason is invisible.
    /// </remarks>
    public event Action? Stalled;

    /// <summary>How long without a frame counts as stopped rather than slow.</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(4);

    public bool IsRunning => _running;

    public int FramesPerSecond { get; set; } = 30;

    /// <summary>True when Wallpaper Engine is running, so this source has something to capture.</summary>
    public static bool IsEngineRunning() => Process.GetProcessesByName(EngineProcessName).Length > 0;

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
            Name = "wallpaper-engine-capture",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    public void Stop()
    {
        _running = false;
        Interlocked.Increment(ref _generation);
    }

    /// <remarks>
    /// The mapping is not touched here: it belongs to the capture thread, which releases it on
    /// the way out. Stopping is enough to get rid of it, and doing it from here instead would
    /// mean pulling the view out from under a read already in progress.
    /// </remarks>
    public void Dispose() => Stop();

    private void Loop(int generation)
    {
        // Nothing to capture until the hook is in and the shared block is up. Both can fail
        // for ordinary reasons — the engine is not running, or it is running at a higher
        // integrity level than us — so a failure here just means no live frames, never a crash.
        if (!EnsureInjected() || !TryMap(out var map, out var view))
        {
            _running = false;
            RaiseUnavailable(generation);
            return;
        }

        // Held as locals, not fields: a stop followed straight away by a start — every other
        // alt-tab does that — leaves this thread winding down while the next one is already
        // mapping its own view, and shared fields would have the two disposing each other's.
        try
        {
            var failed = false;

            while (_running && generation == Volatile.Read(ref _generation))
            {
                var started = Environment.TickCount64;

                try
                {
                    PublishLatest(view);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The mapping can vanish if the engine is restarted; say so, and whoever is
                    // listening decides whether to start over or fall back to another source.
                    failed = true;
                    break;
                }

                var budget = 1000 / Math.Clamp(FramesPerSecond, 1, 120);
                var spent = (int)(Environment.TickCount64 - started);
                Thread.Sleep(Math.Max(5, budget - spent));
            }

            if (failed)
            {
                _running = false;
                RaiseUnavailable(generation);
            }
        }
        finally
        {
            view.Dispose();
            map.Dispose();
        }
    }

    /// <summary>Reports a failed run, unless a newer run has already superseded this one.</summary>
    private void RaiseUnavailable(int generation)
    {
        if (generation == Volatile.Read(ref _generation))
        {
            Unavailable?.Invoke();
        }
    }

    private void PublishLatest(MemoryMappedViewAccessor view)
    {
        if (view.ReadUInt32(OffMagic) != Magic)
        {
            return;
        }

        // Tell the hook we are here and how often to copy. Without a recent heartbeat it stops
        // copying, so an abandoned injection costs Wallpaper Engine nothing.
        view.Write(OffIntervalMs, (uint)(1000 / Math.Clamp(FramesPerSecond, 1, 120)));
        view.Write(OffReaderTickMs, (ulong)Environment.TickCount64);

        var frameIndex = view.ReadInt64(OffFrameIndex);
        if (frameIndex == _lastFrameIndex)
        {
            // The engine is there and the block is valid, but it is not presenting. Said once
            // per run: repeating it while a window stays maximised would be nagging.
            if (!_stallReported
                && _lastFrameAt != 0
                && Environment.TickCount64 - _lastFrameAt > StallAfter.TotalMilliseconds)
            {
                _stallReported = true;
                Stalled?.Invoke();
            }

            return;
        }

        _lastFrameAt = Environment.TickCount64;
        _stallReported = false;

        var width = (int)view.ReadUInt32(OffWidth);
        var height = (int)view.ReadUInt32(OffHeight);
        var stride = (int)view.ReadUInt32(OffStride);
        var channels = view.ReadUInt32(OffChannels);
        var latest = (int)view.ReadUInt32(OffLatest);

        if (width < 2 || height < 2 || latest < 0 || latest >= BufferCount)
        {
            return;
        }

        var length = stride * height;
        if (length <= 0 || length > BufferBytes)
        {
            return;
        }

        _bufferIndex ^= 1;
        var buffer = _buffers[_bufferIndex];
        if (buffer is null || buffer.Length != length)
        {
            buffer = new byte[length];
            _buffers[_bufferIndex] = buffer;
        }

        view.ReadArray(HeaderBytes + latest * BufferBytes, buffer, 0, length);

        // The surface hands us BGRA (0) or RGBA (1); the bitmap on the far side is BGRA, so an
        // RGBA frame — which is what Wallpaper Engine presents — has its red and blue swapped
        // back here rather than inside someone else's render loop. In place, so this buffer is
        // briefly half swizzled: anything that reads it outside the handler below sees those
        // rows with red and blue exchanged, which is why the frame is only on loan for the
        // length of the call (see WallpaperCapture.DesktopFrame).
        if (channels == 1)
        {
            for (var i = 0; i + 2 < length; i += 4)
            {
                (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
            }
        }

        _lastFrameIndex = frameIndex;

        // The wallpaper covers the whole desktop starting at its top-left, so the origin is the
        // desktop origin — zero for the primary screen the wallpaper lives on.
        FrameReady?.Invoke(new WallpaperCapture.DesktopFrame(buffer, width, height, stride, 0, 0));
    }

    /// <summary>
    /// Opens the block the hook publishes into, retrying briefly while it starts up. On success
    /// the mapping belongs to the caller, which must dispose it.
    /// </summary>
    private bool TryMap(
        out MemoryMappedFile map,
        out MemoryMappedViewAccessor view)
    {
        for (var attempt = 0; attempt < 25 && _running; attempt++)
        {
            MemoryMappedFile? opened = null;
            MemoryMappedViewAccessor? accessor = null;

            try
            {
                opened = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
                accessor = opened.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                if (accessor.ReadUInt32(OffMagic) == Magic)
                {
                    map = opened;
                    view = accessor;
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                // The hook has not created the block yet.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                accessor?.Dispose();
                opened?.Dispose();
                break;
            }

            // Either the header is not stamped yet or opening threw: keep neither half.
            accessor?.Dispose();
            opened?.Dispose();

            Thread.Sleep(80);
        }

        map = null!;
        view = null!;
        return false;
    }

    // ---- injection ----

    private static readonly string HookPath = Path.Combine(
        AppContext.BaseDirectory, "RedBloomHook.dll");

    /// <summary>
    /// Puts the capture hook into Wallpaper Engine if it is not already there. Loading the DLL
    /// is what creates the shared block, so a block that already exists means the hook is in.
    /// </summary>
    private bool EnsureInjected()
    {
        if (SharedBlockExists())
        {
            return true;
        }

        if (!File.Exists(HookPath))
        {
            Debug.WriteLine($"Capture hook missing: {HookPath}");
            return false;
        }

        var engine = Process.GetProcessesByName(EngineProcessName).FirstOrDefault();
        if (engine is null)
        {
            return false;
        }

        var injected = Inject(engine.Id, HookPath);

        // Loading runs the hook's startup on a new thread; give it a moment to create the block.
        for (var i = 0; injected && i < 20 && !SharedBlockExists(); i++)
        {
            Thread.Sleep(80);
        }

        return injected && SharedBlockExists();
    }

    private static bool SharedBlockExists()
    {
        try
        {
            using var existing = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Inject(int processId, string dllPath)
    {
        const int ProcessCreateThread = 0x0002;
        const int ProcessVmOperation = 0x0008;
        const int ProcessVmWrite = 0x0020;
        const int ProcessVmRead = 0x0010;
        const int ProcessQueryInformation = 0x0400;

        const int MemCommit = 0x1000;
        const int MemReserve = 0x2000;
        const int PageReadWrite = 0x04;
        const int MemRelease = 0x8000;

        var access = ProcessCreateThread | ProcessVmOperation | ProcessVmWrite | ProcessVmRead | ProcessQueryInformation;
        var process = OpenProcess(access, false, processId);
        if (process == IntPtr.Zero)
        {
            Debug.WriteLine($"OpenProcess failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        var remote = IntPtr.Zero;
        var thread = IntPtr.Zero;

        try
        {
            // kernel32 sits at the same address in every process of a session, so LoadLibraryW's
            // address here is valid in the target — the textbook injection.
            var kernel32 = GetModuleHandle("kernel32.dll");
            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                return false;
            }

            var bytes = System.Text.Encoding.Unicode.GetBytes(dllPath + '\0');
            remote = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)bytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                return false;
            }

            if (!WriteProcessMemory(process, remote, bytes, (IntPtr)bytes.Length, out _))
            {
                return false;
            }

            thread = CreateRemoteThread(process, IntPtr.Zero, IntPtr.Zero, loadLibrary, remote, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                Debug.WriteLine($"CreateRemoteThread failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            WaitForSingleObject(thread, 15000);

            // A zero return from LoadLibraryW means the DLL did not load; anything else is its
            // module handle. The handle is truncated to 32 bits here, which is enough to tell
            // the two apart.
            GetExitCodeThread(thread, out var code);
            return code != 0;
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, IntPtr.Zero, MemRelease);
            }

            if (thread != IntPtr.Zero)
            {
                CloseHandle(thread);
            }

            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, int type, int protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr process, IntPtr attributes, IntPtr stackSize, IntPtr start, IntPtr parameter, int flags, IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    // Unicode: the name is a wide string, and marshalling it as ANSI would hand GetModuleHandleW
    // gibberish and get back a null handle — a null thread start, which is an instant crash of
    // the target. Same reasoning for the file name below.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    // GetProcAddress takes an ANSI symbol name by contract, so this one stays ANSI on purpose.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
