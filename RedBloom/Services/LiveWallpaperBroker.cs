namespace RedBloom.Services;

/// <summary>
/// Relays the live wallpaper frames the main window is already capturing to anywhere else that
/// wants to show them — currently the settings preview, so it can render the real wallpaper
/// instead of a flat colour without standing up a second, duplicate capture.
/// </summary>
public static class LiveWallpaperBroker
{
    public static event Action<WallpaperCapture.DesktopFrame>? Frame;

    public static void Publish(WallpaperCapture.DesktopFrame frame) => Frame?.Invoke(frame);
}
