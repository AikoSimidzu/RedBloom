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

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            RenderMode = System.Windows.Forms.ToolStripRenderMode.Professional,
            ShowImageMargin = false,

            // The legacy square drop shadow is turned off so it does not halo the rounded corners;
            // Windows 11 gives the popup its own shadow that follows the rounding.
            DropShadowEnabled = false,
            Padding = new System.Windows.Forms.Padding(3),
            Font = new System.Drawing.Font("Segoe UI", 9.75f),
        };

        Theme(menu);

        // The menu's popup is its own top-level window; ask DWM to round its corners each time it
        // opens (its handle only exists once shown). Windows 11 rounds and adds the soft shadow;
        // older Windows simply ignores the request.
        menu.Opened += (_, _) => RoundCorners(menu.Handle);

        var show = Item(LocalizationService.T("L_TrayShow"), () => ShowRequested?.Invoke());
        menu.Items.Add(show);

        if (offerElevation)
        {
            menu.Items.Add(Item(LocalizationService.T("L_RestartAdmin"), () => RestartElevatedRequested?.Invoke()));
        }

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add(Item(LocalizationService.T("L_TrayExit"), () => ExitRequested?.Invoke()));

        // The first entry is the plain click's default, shown in bold.
        show.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);

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

    // ---- themed menu ----

    private static System.Windows.Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text)
        {
            Padding = new System.Windows.Forms.Padding(6, 3, 10, 3),
        };

        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Paints the menu in the app's own dark palette instead of the grey Windows default.</summary>
    private static void Theme(System.Windows.Forms.ContextMenuStrip menu)
    {
        var s = ThemeService.Settings;
        var bg = ToDrawing(ThemeService.ParseColor(s.Chrome, System.Windows.Media.Colors.Black));
        var hover = ToDrawing(ThemeService.ParseColor(s.AccentDim, System.Windows.Media.Colors.DimGray));
        var accent = ToDrawing(ThemeService.ParseColor(s.Accent, System.Windows.Media.Colors.OrangeRed));
        var divider = ToDrawing(ThemeService.ParseColor(s.Divider, System.Windows.Media.Colors.Gray));
        var text = ToDrawing(ThemeService.ParseColor(s.TextPrimary, System.Windows.Media.Colors.White));

        menu.BackColor = bg;
        menu.ForeColor = text;
        menu.Renderer = new ThemeRenderer(new ThemeColors(bg, hover, accent, divider), bg, hover, text);
    }

    private static System.Drawing.Color ToDrawing(System.Windows.Media.Color c) =>
        System.Drawing.Color.FromArgb(c.R, c.G, c.B);

    // ---- rounded corners (Windows 11 DWM) ----

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void RoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmwcpRound;

        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (System.Runtime.InteropServices.SEHException)
        {
            // No DWM corner support on this Windows; the menu stays square, which is the old look.
        }
    }

    /// <summary>The dark colours the professional renderer draws the menu with.</summary>
    private sealed class ThemeColors(
        System.Drawing.Color background,
        System.Drawing.Color hover,
        System.Drawing.Color accent,
        System.Drawing.Color divider) : System.Windows.Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => background;

        public override System.Drawing.Color MenuBorder => divider;

        public override System.Drawing.Color MenuItemBorder => accent;

        public override System.Drawing.Color MenuItemSelected => hover;

        public override System.Drawing.Color MenuItemSelectedGradientBegin => hover;

        public override System.Drawing.Color MenuItemSelectedGradientEnd => hover;

        public override System.Drawing.Color MenuItemPressedGradientBegin => hover;

        public override System.Drawing.Color MenuItemPressedGradientEnd => hover;

        public override System.Drawing.Color ImageMarginGradientBegin => background;

        public override System.Drawing.Color ImageMarginGradientMiddle => background;

        public override System.Drawing.Color ImageMarginGradientEnd => background;

        public override System.Drawing.Color SeparatorDark => divider;

        public override System.Drawing.Color SeparatorLight => divider;
    }

    /// <summary>
    /// Draws the item highlight itself — a rounded accent plate — rather than leaving it to the
    /// system, which is where the stock Windows selection was coming from. Text is light, white
    /// where highlighted.
    /// </summary>
    private sealed class ThemeRenderer(
        System.Windows.Forms.ProfessionalColorTable colors,
        System.Drawing.Color background,
        System.Drawing.Color hover,
        System.Drawing.Color text) : System.Windows.Forms.ToolStripProfessionalRenderer(colors)
    {
        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var full = new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size);

            using (var fill = new System.Drawing.SolidBrush(background))
            {
                g.FillRectangle(fill, full);
            }

            var highlighted = e.Item.Selected || e.Item is System.Windows.Forms.ToolStripMenuItem { Pressed: true };

            if (!highlighted)
            {
                return;
            }

            var plate = System.Drawing.Rectangle.Inflate(full, -3, -1);
            var mode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using (var path = Rounded(plate, 6))
            using (var brush = new System.Drawing.SolidBrush(hover))
            {
                g.FillPath(brush, path);
            }

            g.SmoothingMode = mode;
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? System.Drawing.Color.White : text;
            base.OnRenderItemText(e);
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(System.Drawing.Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
