using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RedBloom.Controls;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Terminal;
using RedBloom.Views;

namespace RedBloom;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    /// <summary>Segoe MDL2 "Globe", used for remote tabs.</summary>
    private const string SshGlyph = "";

    private readonly SessionStore _store = new();
    private readonly KnownHostsStore _knownHosts = new();
    private readonly HostKeyPolicy _hostKeyPolicy;
    private string _sessionFilter = string.Empty;

    public MainWindow()
    {
        _hostKeyPolicy = new HostKeyPolicy(_knownHosts, this);

        ShellProfiles = ShellProfile.Discover();
        _store.Load();

        SessionsView = CollectionViewSource.GetDefaultView(_store.Sessions);
        SessionsView.Filter = FilterSession;

        InitializeComponent();
        DataContext = this;

        _store.Sessions.CollectionChanged += OnSessionsChanged;
        Tabs.CollectionChanged += OnTabsChanged;

        Loaded += OnFirstLoad;
    }

    public ObservableCollection<TerminalTab> Tabs { get; } = [];

    public IReadOnlyList<ShellProfile> ShellProfiles { get; }

    public ICollectionView SessionsView { get; }

    public bool HasNoSessions => _store.Sessions.Count == 0;

    /// <summary>A plain new tab opens Command Prompt, as the dropdown's first entry.</summary>
    private ShellProfile? DefaultProfile =>
        ShellProfiles.FirstOrDefault(p => p.Name == "Command Prompt") ?? ShellProfiles.FirstOrDefault();

    // ================= lifecycle =================

    private void OnFirstLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoad;

        if (DefaultProfile is { } profile)
        {
            OpenLocalTab(profile);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        // DWM offers only three fixed corner sizes; "Round" is the larger of the two rounded
        // ones (~8px against RoundSmall's ~4px). A maximized window stays square on its own.
        Interop.Dwm.SetCornerPreference(handle, Interop.Dwm.CornerPreference.Round);
        Interop.MaximizeBounds.Attach(handle);

        HookWallpaperCapture();
        ApplyBackdrops();
        ThemeService.Applied += ApplyBackdrops;
        Closed += (_, _) => ThemeService.Applied -= ApplyBackdrops;
    }

    /// <summary>Positions the background pictures and applies the window-wide alpha.</summary>
    private void ApplyBackdrops()
    {
        var settings = ThemeService.Settings;
        var mode = settings.BackgroundMode;
        var live = mode == BackgroundMode.LiveWallpaper;

        WindowBackdrop.Apply(settings.WindowBackdrop, mode == BackgroundMode.Window || live, live);
        SidebarBackdrop.Apply(settings.SidebarBackdrop, mode == BackgroundMode.Regions);
        TerminalBackdrop.Apply(settings.TerminalBackdrop, mode == BackgroundMode.Regions);

        Topmost = settings.AlwaysOnTop;
        _capture.FramesPerSecond = settings.WallpaperFps;
        UpdateCaptureState();

        var handle = new WindowInteropHelper(this).Handle;
        var seeThrough = settings.WindowOpacity < 0.999;

        // Prefer the documented Windows 11 backdrop; the composition accent policy is the
        // fallback for builds that do not honour it.
        if (!Interop.Dwm.TrySetSystemBackdrop(handle, seeThrough))
        {
            Interop.Dwm.SetAcrylicBackdrop(
                handle,
                ThemeService.ParseColor(settings.Surface, Colors.Black),
                1.0 - settings.WindowOpacity);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        UpdateMaximizeGlyph(); 
        UpdateCaptureState();

        // No inset is needed any more: the window is clamped to the work area on maximise,
        // so nothing hangs off the screen edge.
        RootGrid.Margin = default;
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in Tabs)
        {
            (tab.Content as IDisposable)?.Dispose();
        }

        _capture.FrameReady -= OnWallpaperFrame;
        _capture.Dispose();
        Tabs.Clear();
        base.OnClosed(e);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        EmptyState.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasNoSessions));

    // ================= tabs =================

    private void OpenLocalTab(ShellProfile profile)
    {
        var view = new TerminalView((_, _, _) =>
            Task.FromResult<ITerminalBackend>(new ConPtyBackend(profile)));

        AddTab(view, profile.Name, profile.Glyph, profile.Executable);
    }

    private void OpenSshTab(SshSession session, string? secret)
    {
        // The session is cloned so later edits in the sidebar cannot mutate a live connection.
        var snapshot = session.Clone();
        var view = new TerminalView((_, _, _) => Task.FromResult<ITerminalBackend>(
            new SshBackend(snapshot, secret, _hostKeyPolicy.IsTrusted, _hostKeyPolicy.ApproveAsync)))
        {
            AutoReconnect = snapshot.AutoReconnect,
        };

        AddTab(view, session.Name, SshGlyph, snapshot.DisplayTarget);
    }

    /// <summary>Segoe MDL2 "Settings", used for the appearance tab.</summary>
    private const string SettingsGlyph = "";

    /// <summary>
    /// Opens the appearance page, or brings it forward if it is already open — a second copy
    /// of it would only fight with the first over the same settings.
    /// </summary>
    private void OpenSettingsTab()
    {
        if (Tabs.FirstOrDefault(t => t.Content is SettingsPage) is { } existing)
        {
            SelectTab(existing);
            return;
        }

        AddTab(new SettingsPage(), "Settings", SettingsGlyph, "Appearance settings");
    }

    private void AddTab(FrameworkElement content, string title, string glyph, string toolTip)
    {
        var tab = new TerminalTab(content, title, glyph, toolTip);

        if (content is not TerminalView view)
        {
            content.Visibility = Visibility.Collapsed;
            TerminalHost.Children.Add(content);
            Tabs.Add(tab);
            SelectTab(tab);
            return;
        }

        view.TitleChanged += (_, newTitle) =>
        {
            if (!string.IsNullOrWhiteSpace(newTitle))
            {
                tab.Title = newTitle;
            }
        };

        view.SessionEnded += (_, reason) =>
        {
            tab.HasEnded = true;
            tab.ToolTip = reason;
        };

        view.SessionStarted += (_, _) =>
        {
            tab.HasEnded = false;
            tab.ToolTip = toolTip;
        };

        view.AcceleratorPressed += OnAcceleratorPressed;

        view.Visibility = Visibility.Collapsed;
        TerminalHost.Children.Add(view);
        Tabs.Add(tab);
        SelectTab(tab);
    }

    private void SelectTab(TerminalTab tab)
    {
        foreach (var other in Tabs)
        {
            var selected = ReferenceEquals(other, tab);
            other.IsSelected = selected;
            other.Content.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        tab.View?.FocusTerminal();
    }

    private void CloseTab(TerminalTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasSelected = tab.IsSelected;

        if (tab.View is { } view)
        {
            view.AcceleratorPressed -= OnAcceleratorPressed;
        }

        Tabs.RemoveAt(index);
        TerminalHost.Children.Remove(tab.Content);
        (tab.Content as IDisposable)?.Dispose();

        if (wasSelected && Tabs.Count > 0)
        {
            SelectTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
        }
    }

    private void CycleTab(int offset)
    {
        if (Tabs.Count < 2)
        {
            return;
        }

        var current = Tabs.IndexOf(Tabs.First(t => t.IsSelected));
        var next = ((current + offset) % Tabs.Count + Tabs.Count) % Tabs.Count;
        SelectTab(Tabs[next]);
    }

    private void OnAcceleratorPressed(object? sender, char key)
    {
        switch (key)
        {
            case 'T' when DefaultProfile is { } profile:
                OpenLocalTab(profile);
                break;

            case 'W':
                if (Tabs.FirstOrDefault(t => t.IsSelected) is { } selected)
                {
                    CloseTab(selected);
                }

                break;

            case 'N':
                CycleTab(1);
                break;

            case 'P':
                CycleTab(-1);
                break;

            case 'B':
                SetSidebarCollapsed(!_sidebarCollapsed);
                break;
        }
    }

    // ================= tab strip handlers =================

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        if (DefaultProfile is { } profile)
        {
            OpenLocalTab(profile);
        }
        else
        {
            MessageBox.Show(this, "No shell was found on this machine.", "RedBloom",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShellMenu_Click(object sender, RoutedEventArgs e) => ShellPopup.IsOpen = true;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShellPopup.IsOpen = false;
        OpenSettingsTab();
    }

    private void ShellProfile_Click(object sender, RoutedEventArgs e)
    {
        ShellPopup.IsOpen = false;

        if (sender is FrameworkElement { Tag: ShellProfile profile })
        {
            OpenLocalTab(profile);
        }
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            SelectTab(tab);
            BeginTabDrag(tab, e);
        }
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle
            && sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            CloseTab(tab);
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TerminalTab tab })
        {
            CloseTab(tab);
        }
    }

    // ================= sessions =================

    private bool FilterSession(object item)
    {
        if (_sessionFilter.Length == 0)
        {
            return true;
        }

        if (item is not SshSession session)
        {
            return false;
        }

        return session.Name.Contains(_sessionFilter, StringComparison.OrdinalIgnoreCase)
               || session.Host.Contains(_sessionFilter, StringComparison.OrdinalIgnoreCase)
               || session.Username.Contains(_sessionFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void SessionFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _sessionFilter = SessionFilter.Text.Trim();
        FilterHint.Visibility = SessionFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionsView.Refresh();
    }

    private void AddSession_Click(object sender, RoutedEventArgs e)
    {
        var draft = new SshSession { Name = "New session", Username = Environment.UserName };
        if (SessionDialog.Edit(this, draft, isNew: true))
        {
            _store.Sessions.Add(draft);
            _store.Save();
            SessionList.SelectedItem = draft;
        }
    }

    private void EditSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SshSession session })
        {
            return;
        }

        var draft = session.Clone();
        if (SessionDialog.Edit(this, draft, isNew: false))
        {
            session.CopyFrom(draft);
            _store.Save();
            SessionsView.Refresh();
        }
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SshSession session })
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Delete the saved session \"{session.Name}\"?",
            "RedBloom",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.OK)
        {
            _store.Sessions.Remove(session);
            _store.Save();
        }
    }

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SshSession session)
        {
            Connect(session);
        }
    }

    private void SessionList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SessionList.SelectedItem is SshSession session)
        {
            e.Handled = true;
            Connect(session);
        }
    }

    private void Connect(SshSession session)
    {
        var secret = session.Secret;

        // Password auth with nothing saved: ask once, for this connection only.
        if (session.AuthKind == SshAuthKind.Password && string.IsNullOrEmpty(secret))
        {
            secret = PasswordPrompt.Ask(this, session.DisplayTarget);
            if (secret is null)
            {
                return;
            }
        }

        OpenSshTab(session, secret);
    }

    // ================= live wallpaper =================

    private readonly LiveWallpaperSource _capture = new();

    private void HookWallpaperCapture()
    {
        _capture.FrameReady += OnWallpaperFrame;

        // Capture is decoration: it stops the moment nobody is looking at it.
        Activated += (_, _) => UpdateCaptureState();
        Deactivated += (_, _) => UpdateCaptureState();

        // Purely a transform update, so the background keeps up with a drag frame for frame.
        LocationChanged += (_, _) => UpdateLiveLayout();
        SizeChanged += (_, _) => UpdateLiveLayout();
    }

    private int _desktopOriginX;
    private int _desktopOriginY;

    // Frame pixels per DIP for the last frame received. The whole-desktop capture delivers
    // physical pixels (so this is the screen scale); the engine hook delivers a downscaled
    // frame (so this is smaller). Driving the bitmap DPI from it makes one frame cover exactly
    // the desktop regardless of which source is live.
    private double _frameScale = 1;

    /// <summary>Re-points the live wallpaper at the desktop the window currently sits over.</summary>
    private void UpdateLiveLayout()
    {
        if (!IsLoaded || ThemeService.Settings.BackgroundMode != BackgroundMode.LiveWallpaper)
        {
            return;
        }

        var settings = ThemeService.Settings;
        var aligned = settings.WallpaperDisplay == WallpaperDisplay.AlignedToDesktop;

        // Convert the desktop-pixel origin to DIPs with the frame's own scale, not the window's:
        // the engine hook delivers a downscaled frame, so its pixels-per-DIP differ from the
        // screen's. Both are the same for the whole-desktop capture, so this is correct there too.
        var scale = _frameScale;
        var offsetX = Left - (_desktopOriginX / scale);
        var offsetY = Top - (_desktopOriginY / scale);

        WindowBackdrop.SetLiveLayout(
            aligned,
            offsetX,
            offsetY,
            StretchOf(settings.WindowBackdrop.Stretch),
            new Thickness(
                settings.WallpaperCropLeft,
                settings.WallpaperCropTop,
                settings.WallpaperCropRight,
                settings.WallpaperCropBottom));
    }

    private double DpiScale =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    private static Stretch StretchOf(string name) => name switch
    {
        "Fill" => Stretch.Fill,
        "Uniform" => Stretch.Uniform,
        "None" => Stretch.None,
        _ => Stretch.UniformToFill,
    };

    /// <summary>Runs the capture only while the window is on screen and in front.</summary>
    private void UpdateCaptureState()
    {
        var wanted = ThemeService.Settings.BackgroundMode == BackgroundMode.LiveWallpaper
                     && IsActive
                     && WindowState != WindowState.Minimized
                     && IsVisible;

        if (wanted)
        {
            _capture.Start();
            UpdateLiveLayout();
        }
        else
        {
            _capture.Stop();
        }
    }

    private void OnWallpaperFrame(WallpaperCapture.DesktopFrame frame) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _desktopOriginX = frame.OriginX;
            _desktopOriginY = frame.OriginY;

            // The frame spans the primary screen, whose width in DIPs WPF already knows. Pixels
            // divided by that is the frame's true scale, whether it arrived full-size from the
            // desktop capture or downscaled from the engine hook.
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            _frameScale = screenWidth > 1 ? frame.Width / screenWidth : 1;

            WindowBackdrop.PushFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride, _frameScale);
            UpdateLiveLayout();
        });

    // ================= sidebar =================

    private const double SidebarWidth = 220;

    /// <summary>Collapsed width: wide enough to keep the toggle reachable.</summary>
    private const double SidebarRailWidth = 42;

    private bool _sidebarCollapsed;

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_sidebarCollapsed);

    private void SetSidebarCollapsed(bool collapsed)
    {
        _sidebarCollapsed = collapsed;

        var animation = new GridLengthAnimation
        {
            From = SidebarColumn.ActualWidth,
            To = collapsed ? SidebarRailWidth : SidebarWidth,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };

        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, animation);

        // The brand would otherwise be clipped mid-word as the column narrows.
        var brandVisibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandDot.Visibility = brandVisibility;
        BrandText.Visibility = brandVisibility;

        // Fade the panel out rather than letting the narrowing column slice through it,
        // which would leave half-words standing in the rail.
        SidebarContent.IsHitTestVisible = !collapsed;
        SidebarContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = collapsed ? 0 : 1,
                Duration = TimeSpan.FromMilliseconds(collapsed ? 110 : 200),
                BeginTime = collapsed ? TimeSpan.Zero : TimeSpan.FromMilliseconds(60),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            });

        SidebarToggle.ToolTip = collapsed
            ? "Expand the sidebar (Ctrl+Shift+B)"
            : "Collapse the sidebar (Ctrl+Shift+B)";
    }

    // ================= tab reordering =================

    private TerminalTab? _dragTab;
    private Point _dragOrigin;
    private bool _dragging;

    private void BeginTabDrag(TerminalTab tab, MouseButtonEventArgs e)
    {
        _dragTab = tab;
        _dragOrigin = e.GetPosition(TabStrip);
        _dragging = false;
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTabDrag();
            return;
        }

        var offset = e.GetPosition(TabStrip).X - _dragOrigin.X;

        if (!_dragging)
        {
            if (Math.Abs(offset) < SystemParameters.MinimumHorizontalDragDistance)
            {
                return;
            }

            _dragging = true;
            TabStrip.CaptureMouse();

            if (ContainerOf(_dragTab) is { } lifted)
            {
                Panel.SetZIndex(lifted, 10);
                lifted.Opacity = 0.92;
            }
        }

        if (ContainerOf(_dragTab) is not { } container)
        {
            return;
        }

        SetTranslate(container, offset);
        ReorderIfNeeded(container, offset);
    }

    /// <summary>
    /// Moves the dragged tab past a neighbour once its centre crosses that neighbour's centre,
    /// then slides the displaced tabs into place.
    /// </summary>
    private void ReorderIfNeeded(FrameworkElement container, double offset)
    {
        var draggedIndex = Tabs.IndexOf(_dragTab!);
        if (draggedIndex < 0)
        {
            return;
        }

        var draggedSlot = LayoutInformation.GetLayoutSlot(container);
        var draggedCentre = draggedSlot.X + (draggedSlot.Width / 2) + offset;

        var target = draggedIndex;
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (i == draggedIndex || ContainerOf(Tabs[i]) is not { } other)
            {
                continue;
            }

            var slot = LayoutInformation.GetLayoutSlot(other);
            var centre = slot.X + (slot.Width / 2);

            if ((i < draggedIndex && draggedCentre < centre) || (i > draggedIndex && draggedCentre > centre))
            {
                target = i;
            }
        }

        if (target == draggedIndex)
        {
            return;
        }

        // Remember where everything sat so the move can be animated from there.
        var before = new Dictionary<TerminalTab, double>();
        foreach (var tab in Tabs)
        {
            if (ContainerOf(tab) is { } element)
            {
                before[tab] = LayoutInformation.GetLayoutSlot(element).X;
            }
        }

        Tabs.Move(draggedIndex, target);
        TabStrip.UpdateLayout();

        foreach (var tab in Tabs)
        {
            if (ContainerOf(tab) is not { } element || !before.TryGetValue(tab, out var oldX))
            {
                continue;
            }

            var delta = oldX - LayoutInformation.GetLayoutSlot(element).X;
            if (Math.Abs(delta) < 0.5)
            {
                continue;
            }

            if (ReferenceEquals(tab, _dragTab))
            {
                // Fold the layout shift into the drag origin so the tab stays under the cursor.
                _dragOrigin = new Point(_dragOrigin.X - delta, _dragOrigin.Y);
                SetTranslate(element, offset + delta);
            }
            else
            {
                SlideToRest(element, delta);
            }
        }
    }

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndTabDrag();

    private void TabStrip_LostMouseCapture(object sender, MouseEventArgs e) => EndTabDrag();

    private void EndTabDrag()
    {
        var tab = _dragTab;
        _dragTab = null;

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        TabStrip.ReleaseMouseCapture();

        if (tab is not null && ContainerOf(tab) is { } container)
        {
            Panel.SetZIndex(container, 0);
            container.Opacity = 1;
            SlideToRest(container, GetTranslate(container).X);
        }
    }

    private FrameworkElement? ContainerOf(TerminalTab tab) =>
        TabStrip.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement;

    private static TranslateTransform GetTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing)
        {
            return existing;
        }

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void SetTranslate(FrameworkElement element, double x)
    {
        var transform = GetTranslate(element);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = x;
    }

    /// <summary>Slides an element from the given offset back to its laid-out position.</summary>
    private static void SlideToRest(FrameworkElement element, double from)
    {
        var transform = GetTranslate(element);
        transform.X = from;
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation
            {
                From = from,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        transform.X = 0;
    }

    // ================= window chrome =================

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private bool _filledScreen;
    private Rect _boundsBeforeFill;

    /// <summary>
    /// Wallpaper Engine stops animating whenever another window is maximised, so with a live
    /// wallpaper the window fills the work area by size alone and stays in the normal state.
    /// Verified: the same window at the same size animates when it is not maximised and
    /// freezes the moment it is.
    /// </summary>
    private bool PrefersFillOverMaximize =>
        ThemeService.Settings.BackgroundMode == BackgroundMode.LiveWallpaper;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (PrefersFillOverMaximize || _filledScreen)
        {
            ToggleFillScreen();
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ToggleFillScreen()
    {
        if (_filledScreen)
        {
            Left = _boundsBeforeFill.X;
            Top = _boundsBeforeFill.Y;
            Width = _boundsBeforeFill.Width;
            Height = _boundsBeforeFill.Height;
            _filledScreen = false;
            UpdateMaximizeGlyph();
            return;
        }

        var work = Interop.MaximizeBounds.GetWorkArea(new WindowInteropHelper(this).Handle);
        if (work is not { } area)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        _boundsBeforeFill = new Rect(Left, Top, Width, Height);

        var scale = DpiScale;
        Left = area.X / scale;
        Top = area.Y / scale;
        Width = area.Width / scale;
        Height = area.Height / scale;

        _filledScreen = true;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        var filled = WindowState == WindowState.Maximized || _filledScreen;
        MaximizeButton.Content = filled ? "" : "";
        MaximizeButton.ToolTip = filled ? "Restore" : "Maximize";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================= INotifyPropertyChanged =================

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}



