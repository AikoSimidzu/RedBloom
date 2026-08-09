namespace RedBloom.Services;

/// <summary>
/// Feeds the live wallpaper from the best source available: a clean, icon-free capture out of
/// Wallpaper Engine when it is running, or the whole-desktop <see cref="WallpaperCapture"/>
/// (which unavoidably includes the desktop icons) otherwise.
/// </summary>
/// <remarks>
/// The choice is made each time capture starts, so switching Wallpaper Engine on or off and
/// toggling the window focus is enough to pick the source up again — there is nothing to
/// configure. Both sources hand back the same <see cref="WallpaperCapture.DesktopFrame"/>, so
/// the window that draws them does not care which one is live.
/// </remarks>
public sealed class LiveWallpaperSource : IDisposable
{
    private readonly WallpaperCapture _desktop = new();
    private readonly WallpaperEngineCapture _engine = new();

    // Which source is live, and whether frames are wanted at all. Both are read from the
    // engine source's capture thread when it reports a failure, so they are only touched
    // under this lock.
    private readonly object _gate = new();
    private object? _active;
    private bool _wanted;

    public event Action<WallpaperCapture.DesktopFrame>? FrameReady;

    private int _framesPerSecond = 30;

    public int FramesPerSecond
    {
        get => _framesPerSecond;
        set
        {
            _framesPerSecond = value;
            _desktop.FramesPerSecond = value;
            _engine.FramesPerSecond = value;
        }
    }

    /// <summary>True when the current frames come from Wallpaper Engine, icon-free.</summary>
    public bool UsingEngine
    {
        get
        {
            lock (_gate)
            {
                return ReferenceEquals(_active, _engine);
            }
        }
    }

    public LiveWallpaperSource()
    {
        _desktop.FrameReady += OnFrame;
        _engine.FrameReady += OnFrame;
        _engine.Unavailable += OnEngineUnavailable;
    }

    public void Start()
    {
        lock (_gate)
        {
            _wanted = true;

            // Prefer the engine hook; deciding up front avoids injecting for nothing.
            if (WallpaperEngineCapture.IsEngineRunning())
            {
                Switch(_engine);
                _engine.Start();
            }
            else
            {
                Switch(_desktop);
                _desktop.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            _engine.Stop();
            _desktop.Stop();
            _active = null;
        }
    }

    /// <summary>
    /// Takes over with the whole-desktop capture when the hook cannot be used after all.
    /// </summary>
    /// <remarks>
    /// Injection runs on the engine source's own thread and takes a moment, so <see cref="Start"/>
    /// cannot know whether it worked — asking there only ever saw the flag the source had just
    /// raised, which is why an engine running at a higher integrity level than us used to leave
    /// the background frozen instead of falling back. The answer arrives here instead, whenever
    /// it is ready. Frames then carry the desktop icons, which is the point of preferring the
    /// hook, but that beats showing nothing.
    /// </remarks>
    private void OnEngineUnavailable()
    {
        lock (_gate)
        {
            // A stop, or a switch someone made in the meantime, settles it: the run that failed
            // is no longer the one anybody is waiting on.
            if (!_wanted || !ReferenceEquals(_active, _engine))
            {
                return;
            }

            Switch(_desktop);
            _desktop.Start();
        }
    }

    private void Switch(object source)
    {
        if (ReferenceEquals(_active, source))
        {
            return;
        }

        if (ReferenceEquals(_active, _engine))
        {
            _engine.Stop();
        }
        else if (ReferenceEquals(_active, _desktop))
        {
            _desktop.Stop();
        }

        _active = source;
    }

    // Both sources feed the same handler; the inactive one is stopped, and a stray frame that
    // was already in flight is the same desktop wallpaper either way, so it is safe to forward.
    private void OnFrame(WallpaperCapture.DesktopFrame frame) => FrameReady?.Invoke(frame);

    public void Dispose()
    {
        _engine.Unavailable -= OnEngineUnavailable;
        _engine.FrameReady -= OnFrame;
        _desktop.FrameReady -= OnFrame;
        _engine.Dispose();
        _desktop.Dispose();
    }
}
