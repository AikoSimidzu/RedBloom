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
    private object? _active;

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
    public bool UsingEngine => ReferenceEquals(_active, _engine);

    public LiveWallpaperSource()
    {
        _desktop.FrameReady += OnFrame;
        _engine.FrameReady += OnFrame;
    }

    public void Start()
    {
        // Prefer the engine hook. If Wallpaper Engine is not running, or the hook cannot be
        // injected, it stops itself and the desktop capture takes over on the next tick — but
        // deciding up front avoids injecting for nothing.
        if (WallpaperEngineCapture.IsEngineRunning())
        {
            Switch(_engine);
            _engine.Start();

            // The engine source turns itself off when injection is impossible (for example the
            // engine runs elevated and we do not); fall back rather than show nothing.
            if (!_engine.IsRunning)
            {
                Switch(_desktop);
                _desktop.Start();
            }
        }
        else
        {
            Switch(_desktop);
            _desktop.Start();
        }
    }

    public void Stop()
    {
        _engine.Stop();
        _desktop.Stop();
        _active = null;
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
        _engine.Dispose();
        _desktop.Dispose();
    }
}
