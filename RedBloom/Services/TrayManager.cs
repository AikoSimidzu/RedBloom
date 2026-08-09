namespace RedBloom.Services;

/// <summary>
/// The notification-area icon. It appears only while the window is hidden to the tray, carries a
/// menu to bring the window back, restart elevated, or quit, and is driven entirely through
/// fully-qualified WinForms types so the rest of the app stays WPF-only.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayManager(string exePath, bool offerElevation)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "RedBloom",
            Visible = false,
        };

        try
        {
            _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.FileNotFoundException)
        {
            _icon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();

        var show = new System.Windows.Forms.ToolStripMenuItem(LocalizationService.T("L_TrayShow"));
        show.Click += (_, _) => ShowRequested?.Invoke();
        menu.Items.Add(show);

        if (offerElevation)
        {
            var elevate = new System.Windows.Forms.ToolStripMenuItem(LocalizationService.T("L_RestartAdmin"));
            elevate.Click += (_, _) => RestartElevatedRequested?.Invoke();
            menu.Items.Add(elevate);
        }

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exit = new System.Windows.Forms.ToolStripMenuItem(LocalizationService.T("L_TrayExit"));
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon.ContextMenuStrip = menu;

        // Double-click is the well-worn way to bring a trayed window back.
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>Bring the window back — double-click or the menu's Show entry.</summary>
    public event Action? ShowRequested;

    /// <summary>Quit the whole app from the tray menu.</summary>
    public event Action? ExitRequested;

    /// <summary>Relaunch elevated from the tray menu.</summary>
    public event Action? RestartElevatedRequested;

    public void ShowIcon() => _icon.Visible = true;

    public void HideIcon() => _icon.Visible = false;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
