using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
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

        // Published for the parts that resolve an attached connection long after the picker has
        // gone — a chat turn running in the background has no way to reach this window.
        SessionCatalog.Store = _store;

        // The tunnel opener needs the same host-key judgement the terminal uses; without it an
        // unknown host would be refused silently instead of being put to the user.
        Services.Ai.AgentTunnel.IsTrusted = _hostKeyPolicy.IsTrusted;
        Services.Ai.AgentTunnel.ApproveAsync = _hostKeyPolicy.ApproveAsync;
        ChatStore.Load();
        RoomStore.Load();

        SessionsView = CollectionViewSource.GetDefaultView(_store.Sessions);
        SessionsView.Filter = FilterSession;

        InitializeComponent();
        DataContext = this;

        _store.Sessions.CollectionChanged += OnSessionsChanged;
        Tabs.CollectionChanged += OnTabsChanged;

        // The recent-files list follows what agents produce, on whatever thread they run on, so the
        // refresh is marshalled back and only done while the browser is actually showing it.
        AgentFiles.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_filesMode && FilesRecentButton?.IsChecked == true)
            {
                RefreshFiles();
            }
        });

        // A chat becomes real the first time it is answered, which happens in a tab rather than
        // in the sidebar — so the list follows the store instead of being rebuilt by hand.
        ChatStore.Chats.CollectionChanged += (_, _) => RefreshChats();
        RoomStore.Rooms.CollectionChanged += (_, _) => RefreshRooms();

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
            // Launched from the Explorer "Open RedBloom here" entry, the folder arrives as an
            // argument; the first tab then opens the shell right there.
            var startIn = StartupDirectory();
            OpenLocalTab(startIn is null ? profile : profile.WithStartingDirectory(startIn));
        }
    }

    /// <summary>The folder passed on the command line, if it is one that exists.</summary>
    private static string? StartupDirectory()
    {
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            var trimmed = arg.Trim().Trim('"');
            if (trimmed.Length > 0 && System.IO.Directory.Exists(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        // DWM offers only three fixed corner sizes; "Round" is the larger of the two rounded
        // ones (~8px against RoundSmall's ~4px). A maximized window stays square on its own.
        Interop.Dwm.SetCornerPreference(handle, Interop.Dwm.CornerPreference.Round);
        Interop.MaximizeBounds.Attach(handle, this);

        HookWallpaperCapture();
        ApplyBackdrops();
        ThemeService.Applied += ApplyBackdrops;
        Closed += (_, _) => ThemeService.Applied -= ApplyBackdrops;

        // The AI page raises this rather than opening tabs itself: pages are content, and the
        // window is what owns the tab strip.
        AiSettingsPage.LaunchRequested += OpenAgentTab;
        LocalModelsPage.LaunchRequested += OpenAgentTab;

        SetupTray();

        // The offer is pointless once we already have the rights, however they were got.
        UpdateElevationMenu();
        ElevatedHost.StateChanged += () => Dispatcher.Invoke(UpdateElevationMenu);
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

        // Static, so leaving it subscribed would keep this window alive past its close.
        AiSettingsPage.LaunchRequested -= OpenAgentTab;

        _capture.FrameReady -= OnWallpaperFrame;
        _capture.Dispose();
        _tray?.Dispose();
        Tabs.Clear();
        base.OnClosed(e);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        EmptyState.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasNoSessions));

    // ================= tabs =================

    /// <summary>
    /// How to spawn another pane like a given one: a local shell reopens the same profile, an
    /// SSH pane opens a fresh independent connection to the same session.
    /// </summary>
    private abstract record PaneSource;

    private sealed record LocalSource(ShellProfile Profile) : PaneSource;

    private sealed record SshSource(SshSession Session, string? Secret) : PaneSource;


    // How each live pane can be reproduced, for the split shortcuts.
    private readonly Dictionary<TerminalView, PaneSource> _paneSources = [];

    private TerminalView CreateView(PaneSource source) => source switch
    {
        LocalSource local => new TerminalView((_, _, _) =>
            Task.FromResult<ITerminalBackend>(new ConPtyBackend(local.Profile))),

        // Cloned per pane so a later sidebar edit cannot mutate a live connection.
        SshSource ssh => new TerminalView((_, _, _) => Task.FromResult<ITerminalBackend>(
            new SshBackend(ssh.Session.Clone(), ssh.Secret, _hostKeyPolicy.IsTrusted, _hostKeyPolicy.ApproveAsync)))
        {
            AutoReconnect = ssh.Session.AutoReconnect,
        },


        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private void OpenLocalTab(ShellProfile profile)
    {
        var source = new LocalSource(profile);
        var view = CreateView(source);
        _paneSources[view] = source;

        var tab = AddTerminalTab(view, profile.Name, profile.Glyph, profile.Executable);

        // A local tab's card look is runtime-only; there is no saved session to keep it in.
        tab.Card = new TabCardStyle();
    }

    private void OpenSshTab(SshSession session, string? secret)
    {
        var source = new SshSource(session.Clone(), secret);
        var view = CreateView(source);
        _paneSources[view] = source;

        var tab = AddTerminalTab(view, session.Name, SshGlyph, source.Session.DisplayTarget);

        // The tab edits the saved session's own card, so a right-click tweak persists.
        tab.Session = session;
        tab.Card = session.TabCard;
    }

    /// <summary>Starts a fresh conversation with an agent.</summary>
    private void OpenAgentTab(AiAgent agent) =>
        OpenChatTab(agent, new ChatSession { AgentId = agent.Id });

    /// <summary>
    /// Opens one conversation as its own tab.
    /// </summary>
    /// <remarks>
    /// A page tab rather than a terminal one: the chat draws itself and has nothing a split
    /// would divide. Closing the tab disposes the content, which is what ends the session.
    /// </remarks>
    private void OpenChatTab(AiAgent agent, ChatSession chat)
    {
        // One tab per chat: a second view of the same conversation would have the two writing
        // over each other's history.
        if (Tabs.FirstOrDefault(t => t.Chat?.Id == chat.Id) is { } existing)
        {
            SelectTab(existing);
            return;
        }

        var tab = AddTab(
            new AgentChatView(agent, chat),
            chat.Title.Length > 0 ? chat.Title : agent.Name,
            AiGlyph,
            $"{agent.Name} · {agent.Model}");

        tab.Chat = chat;
        tab.Card = chat.Card;

        // The card is edited through the same right-click popup a session's is, so a change
        // there has to reach the chat's own file.
        chat.Card.Changed += Save;
        chat.Changed += Rename;

        void Save() => ChatStore.Save(chat);

        void Rename()
        {
            if (chat.Title.Length > 0)
            {
                tab.Title = chat.Title;
            }
        }
    }

    /// <summary>Which agent the AI panel is showing chats for.</summary>
    private AiAgent? SelectedAgent => AgentPicker.SelectedItem as AiAgent;

    private void SidebarMode_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during XAML load, before the panels exist.
        if (SshPanel is null || AiPanel is null || RoomsPanel is null)
        {
            return;
        }

        var ai = AiModeButton.IsChecked == true;
        var rooms = RoomsModeButton.IsChecked == true;
        var ssh = !ai && !rooms;

        SshPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        RoomsPanel.Visibility = rooms ? Visibility.Visible : Visibility.Collapsed;

        if (ai)
        {
            RefreshAgents();
        }
        else if (rooms)
        {
            RefreshRooms();
        }
    }

    // ---- files browser ----

    private bool _filesMode;
    private string _filesDir = Services.AgentFiles.Root;

    /// <summary>The files button by the collapse control: toggles the browser in and out.</summary>
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        _filesMode = !_filesMode;
        ApplyFilesMode();
    }

    private void ApplyFilesMode()
    {
        if (FilesPanel is null || ModeToggle is null)
        {
            return;
        }

        if (_filesMode)
        {
            ModeToggle.Visibility = Visibility.Collapsed;
            SshPanel.Visibility = Visibility.Collapsed;
            AiPanel.Visibility = Visibility.Collapsed;
            RoomsPanel.Visibility = Visibility.Collapsed;
            FilesPanel.Visibility = Visibility.Visible;
            FilesToggle.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            RefreshFiles();
        }
        else
        {
            FilesPanel.Visibility = Visibility.Collapsed;
            ModeToggle.Visibility = Visibility.Visible;
            FilesToggle.ClearValue(ForegroundProperty);

            // Restore whichever of SSH/AI/Rooms was last chosen.
            SidebarMode_Changed(this, new RoutedEventArgs());
        }
    }

    private void FilesTab_Changed(object sender, RoutedEventArgs e)
    {
        if (FilesList is not null && _filesMode)
        {
            RefreshFiles();
        }
    }

    private void FilesUp_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.GetParent(_filesDir) is { } parent)
        {
            _filesDir = parent.FullName;
            RefreshFiles();
        }
    }

    private void FilesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is not FileRow row)
        {
            return;
        }

        if (row.IsDir)
        {
            _filesDir = row.FullPath;
            RefreshFiles();
        }
        else
        {
            OpenFileTab(row.FullPath);
        }
    }

    private void RefreshFiles()
    {
        if (FilesList is null)
        {
            return;
        }

        var recent = FilesRecentButton.IsChecked == true;
        FilesPathRow.Visibility = recent ? Visibility.Collapsed : Visibility.Visible;

        var rows = new List<FileRow>();

        try
        {
            if (recent)
            {
                foreach (var path in Services.AgentFiles.Recent())
                {
                    rows.Add(FileRow.ForFile(new FileInfo(path)));
                }
            }
            else
            {
                FilesPath.Text = _filesDir;
                FilesUpButton.IsEnabled = Directory.GetParent(_filesDir) is not null;

                var dir = new DirectoryInfo(_filesDir);

                if (dir.Exists)
                {
                    foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        rows.Add(FileRow.ForDir(sub));
                    }

                    foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        rows.Add(FileRow.ForFile(file));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A folder that cannot be read simply shows empty rather than throwing.
        }

        FilesList.ItemsSource = rows;
        NoFiles.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenFileTab(string path)
    {
        if (Tabs.FirstOrDefault(t => string.Equals(t.ToolTip, path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            SelectTab(existing);
            return;
        }

        AddTab(new Controls.FileEditorView(path), Path.GetFileName(path), "", path);
    }

    /// <summary>One entry in the file browser — a folder, or a file with its size and time.</summary>
    private sealed record FileRow(string Name, string Detail, string Glyph, string FullPath, bool IsDir)
    {
        public static FileRow ForDir(DirectoryInfo d) =>
            new(d.Name, LocalizationService.T("L_FilesFolder"), "", d.FullName, true);

        public static FileRow ForFile(FileInfo f) =>
            new(f.Name, Describe(f), "", f.FullName, false);

        private static string Describe(FileInfo f)
        {
            try
            {
                var kb = f.Length / 1024.0;
                var size = kb < 1024 ? $"{kb:0.#} KB" : $"{kb / 1024:0.#} MB";
                return $"{size} · {f.LastWriteTime:d MMM HH:mm}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Fills the agent picker, keeping the current choice where it still exists.
    /// </summary>
    /// <remarks>
    /// The configured agents go in at once and the models on this machine are added when the
    /// look-round answers: finding those means asking the engine whether it is listening, and
    /// the picker should not sit empty for a second while that happens.
    /// </remarks>
    private void RefreshAgents()
    {
        // The downloaded files are a directory listing, so they go in with the saved agents
        // straight away; only what the engine is serving has to be waited for. The installed
        // command-line tool, if there is one, is a file check and costs nothing either.
        Show([.. ThemeService.Settings.Agents, .. Stock(), .. Fresh(Services.Ai.LocalAgents.FromFiles())]);

        _ = AddLocalAgentsAsync();

        void Show(List<AiAgent> agents)
        {
            var wanted = SelectedAgent?.Id;
            AgentPicker.ItemsSource = agents;

            AgentPicker.SelectedItem =
                agents.FirstOrDefault(a => a.Id == wanted) ?? agents.FirstOrDefault();

            RefreshChats();
        }

        // A discovered local model that has been adopted into the saved list is already in the
        // picker under that entry; listing the found copy too would show it twice.
        static IEnumerable<AiAgent> Fresh(IReadOnlyList<AiAgent> discovered)
        {
            var saved = ThemeService.Settings.Agents.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            return discovered.Where(a => !saved.Contains(a.Id));
        }

        // Agents that are simply there when the thing behind them is installed, with nothing to
        // configure: no key, no endpoint, and credentials the tool keeps itself.
        static IEnumerable<AiAgent> Stock() =>
            Services.Ai.ClaudeCli.IsInstalled ? [Services.Ai.ClaudeCli.Agent] : [];

        async Task AddLocalAgentsAsync()
        {
            var local = await Services.Ai.LocalAgents.DiscoverAsync().ConfigureAwait(true);

            // The saved list is re-read rather than captured: the look-round takes a moment, and
            // an agent may have been added or removed in it.
            var offered = Fresh(local).ToList();

            if (offered.Count > 0)
            {
                Show([.. ThemeService.Settings.Agents, .. Stock(), .. offered]);
            }
        }
    }

    private void AgentPicker_Changed(object sender, SelectionChangedEventArgs e) => RefreshChats();

    private void RefreshChats()
    {
        // Reached from the store's own event, which can fire before the panel is loaded.
        if (ChatList is null || NoChats is null)
        {
            return;
        }

        var chats = SelectedAgent is { } agent ? ChatStore.ForAgent(agent.Id).ToList() : [];
        ChatList.ItemsSource = chats;

        NoChats.Visibility = chats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAgent is { } agent)
        {
            OpenAgentTab(agent.Clone());
        }
    }

    // ---- rooms ----

    private void RefreshRooms()
    {
        if (RoomList is null || NoRooms is null)
        {
            return;
        }

        var rooms = RoomStore.Rooms.OrderByDescending(r => r.UpdatedAt).ToList();
        RoomList.ItemsSource = rooms;
        NoRooms.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditRoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChatRoom room } && RoomDialog.Edit(this, room))
        {
            RoomStore.Save(room);
            RefreshRooms();
            RefreshOpenRoom(room);
        }
    }

    /// <summary>Tells a room's open tab, if it has one, that its roster or title has changed.</summary>
    private void RefreshOpenRoom(ChatRoom room)
    {
        if (Tabs.FirstOrDefault(t => t.Room?.Id == room.Id)?.Content is RoomChatView view)
        {
            view.RefreshParticipants();
        }
    }

    private void DeleteRoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChatRoom room })
        {
            RoomStore.Delete(room);
            RefreshRooms();
        }
    }

    private void NewRoom_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeService.Settings.Agents.Count == 0)
        {
            MessageBox.Show(this, LocalizationService.T("L_RoomNeedAgents"),
                LocalizationService.T("L_RoomTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var room = new ChatRoom();

        if (RoomDialog.Edit(this, room))
        {
            RoomStore.Save(room);
            RefreshRooms();
            OpenRoomTab(room);
        }
    }

    private void RoomList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RoomList.SelectedItem is ChatRoom room)
        {
            OpenRoomTab(room);
        }
    }

    private void RoomList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (RoomList.SelectedItem is not ChatRoom room)
        {
            return;
        }

        var menu = new ContextMenu();

        var edit = new MenuItem { Header = LocalizationService.T("L_RoomEdit") };
        edit.Click += (_, _) =>
        {
            if (RoomDialog.Edit(this, room))
            {
                RoomStore.Save(room);
                RefreshRooms();
                RefreshOpenRoom(room);
            }
        };

        var delete = new MenuItem { Header = LocalizationService.T("L_Delete") };
        delete.Click += (_, _) =>
        {
            RoomStore.Delete(room);
            RefreshRooms();
        };

        menu.Items.Add(edit);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void OpenRoomTab(ChatRoom room)
    {
        if (Tabs.FirstOrDefault(t => t.Room?.Id == room.Id) is { } existing)
        {
            SelectTab(existing);
            return;
        }

        var tab = AddTab(
            new RoomChatView(room),
            room.Title,
            AiGlyph,
            room.Title);

        tab.Room = room;
        tab.Card = room.Card;

        room.Card.Changed += () => RoomStore.Save(room);
        room.Changed += () =>
        {
            tab.Title = room.Title;
            RefreshRooms();
        };
    }

    private void ChatList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChatList.SelectedItem is ChatSession chat && SelectedAgent is { } agent)
        {
            OpenChatTab(agent.Clone(), chat);
        }
    }

    private void ChatList_KeyDown(object sender, KeyEventArgs e)
    {
        if (ChatList.SelectedItem is not ChatSession chat)
        {
            return;
        }

        if (e.Key == Key.Enter && SelectedAgent is { } agent)
        {
            OpenChatTab(agent.Clone(), chat);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            RemoveChat(chat);
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            RenameChat(chat);
            e.Handled = true;
        }
    }

    private void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChatSession chat })
        {
            RemoveChat(chat);
        }
    }

    /// <summary>
    /// Names a chat and the agent inside it, from the list where both are seen.
    /// </summary>
    /// <remarks>
    /// The tab's own tab is only its paintwork; a name is how the chat is found, which is a
    /// sidebar matter. Reached with F2 or from the rename button on the row.
    /// </remarks>
    private void RenameChat(ChatSession chat)
    {
        var dialog = new Views.ChatNamesDialog(chat) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ChatStore.Save(chat);
        RefreshChats();

        // An open tab is still showing the old name until it is told.
        if (Tabs.FirstOrDefault(t => t.Chat?.Id == chat.Id) is { } tab)
        {
            tab.Title = chat.Title;

            if (tab.Content is AgentChatView view)
            {
                view.RefreshBotName();
            }
        }
    }

    private void RenameChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChatSession chat })
        {
            RenameChat(chat);
        }
    }

    /// <summary>Deletes a chat, closing its tab first so nothing writes it back afterwards.</summary>
    private void RemoveChat(ChatSession chat)
    {
        if (Tabs.FirstOrDefault(t => t.Chat?.Id == chat.Id) is { } open)
        {
            CloseTab(open);
        }

        ChatStore.Delete(chat);
        RefreshChats();
    }

    /// <summary>Segoe MDL2 "Settings", used for the appearance tab.</summary>
    private const string SettingsGlyph = "";

    /// <summary>Segoe MDL2 "Robot", used for the AI page and for agent tabs.</summary>
    private const string AiGlyph = "\uE99A";

    /// <summary>Opens the AI page, or brings the open one forward.</summary>
    private void OpenAiSettingsTab()
    {
        if (Tabs.FirstOrDefault(t => t.Content is AiSettingsPage) is { } existing)
        {
            SelectTab(existing);
            return;
        }

        AddTab(new AiSettingsPage(), LocalizationService.T("L_AiSettings"), AiGlyph,
            LocalizationService.T("L_AiTitle"));
    }

    /// <summary>Segoe MDL2 "Processor", used for the local models page.</summary>
    private static readonly string LocalGlyph = ((char)0xE964).ToString();

    private void OpenLocalModelsTab()
    {
        if (Tabs.FirstOrDefault(t => t.Content is LocalModelsPage) is { } existing)
        {
            SelectTab(existing);
            return;
        }

        AddTab(new LocalModelsPage(), LocalizationService.T("L_LocalModels"), LocalGlyph,
            LocalizationService.T("L_LocalModels"));
    }

    private void LocalModels_Click(object sender, RoutedEventArgs e)
    {
        ShellPopup.IsOpen = false;
        OpenLocalModelsTab();
    }

    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        // Every other entry in this menu closes it on the way out; this one was missing it.
        ShellPopup.IsOpen = false;
        OpenAiSettingsTab();
    }

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

        AddTab(new SettingsPage(), LocalizationService.T("L_Settings"), SettingsGlyph,
            LocalizationService.T("L_Appearance"));
    }

    /// <summary>Adds a page tab such as the appearance settings — anything that is not a terminal.</summary>
    private TerminalTab AddTab(FrameworkElement content, string title, string glyph, string toolTip)
    {
        var tab = new TerminalTab(content, title, glyph, toolTip);
        content.Visibility = Visibility.Collapsed;
        TerminalHost.Children.Add(content);
        Tabs.Add(tab);
        SelectTab(tab);
        return tab;
    }

    /// <summary>Adds a terminal tab, wrapping the first pane in a split container it can grow into.</summary>
    private TerminalTab AddTerminalTab(TerminalView view, string title, string glyph, string toolTip)
    {
        var container = new SplitContainer(view)
        {
            // Keep the panes (WebView2 surfaces) off the window's right and bottom resize
            // borders so the grip stays reachable. The gap lands over the host's terminal fill,
            // so it matches the console edge instead of flashing the background picture.
            Margin = new Thickness(0, 0, 6, 6),
        };
        var tab = new TerminalTab(container, title, glyph, toolTip);

        // The close button drawn on a split routes back here to close that pane, or the tab
        // when it was the last one.
        container.PaneCloseRequested += pane => ClosePaneOrTab(tab, container, pane);

        WireOriginPane(tab, view, toolTip);

        container.Visibility = Visibility.Collapsed;
        TerminalHost.Children.Add(container);
        Tabs.Add(tab);
        SelectTab(tab);
        return tab;
    }

    /// <summary>Wires the pane that speaks for the tab: its title, tooltip and ended state.</summary>
    private void WireOriginPane(TerminalTab tab, TerminalView view, string toolTip)
    {
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
        view.SshCommandTyped += (_, session) => OnSshCommandTyped(session);
    }

    // A split pane does not drive the tab's title or ended state — the tab still stands for its
    // first pane — so it only needs the shortcut hook and the typed-ssh offer.
    private void WireSplitPane(TerminalView view)
    {
        view.AcceleratorPressed += OnAcceleratorPressed;
        view.SshCommandTyped += (_, session) => OnSshCommandTyped(session);
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

        if (tab.Panes is { } container)
        {
            foreach (var pane in container.Panes.ToList())
            {
                pane.AcceleratorPressed -= OnAcceleratorPressed;
                _paneSources.Remove(pane);
            }
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

            // D adds a split, A closes the focused one. Alt keeps the same SSH connection
            // (another shell on it); Shift opens a new independent session.
            case 'd':
                AddSplit(sender, sameConnection: true);
                break;
            case 'D':
                AddSplit(sender, sameConnection: false);
                break;
            case 'a':
            case 'A':
                CloseFocusedPane(sender);
                break;
        }
    }

    /// <summary>Finds the tab and split holder a pane lives in, and marks the pane active.</summary>
    private (TerminalTab Tab, SplitContainer Container)? LocatePane(object? sender)
    {
        if (sender is not TerminalView view)
        {
            return null;
        }

        foreach (var tab in Tabs)
        {
            if (tab.Panes is { } container && container.Panes.Contains(view))
            {
                container.SetActive(view);
                return (tab, container);
            }
        }

        return null;
    }

    /// <summary>Adds a split to the tab the pane belongs to, up to the four-pane ceiling.</summary>
    private void AddSplit(object? sender, bool sameConnection) =>
        AddSplitTo(LocatePane(sender), sameConnection);

    /// <summary>Adds a split to a located tab. <paramref name="sameConnection"/> reuses the SSH login.</summary>
    private void AddSplitTo((TerminalTab Tab, SplitContainer Container)? located, bool sameConnection)
    {
        if (located is not { } target || target.Container.ActiveView is not { } active)
        {
            return;
        }

        if (!target.Container.CanAdd)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        TerminalView pane;
        if (sameConnection)
        {
            var connection = active.SshConnection;
            if (connection is null || !connection.IsConnected)
            {
                // Only a live SSH pane has a connection to share.
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            pane = new TerminalView((_, _, _) =>
                Task.FromResult<ITerminalBackend>(new SshBackend(connection)));

            if (_paneSources.TryGetValue(active, out var source))
            {
                _paneSources[pane] = source;
            }
        }
        else
        {
            if (!_paneSources.TryGetValue(active, out var source))
            {
                return;
            }

            pane = CreateView(source);
            _paneSources[pane] = source;
        }

        WireSplitPane(pane);
        target.Container.Add(pane);
        pane.FocusTerminal();
    }

    /// <summary>Closes the focused pane, or the whole tab when it is the only one left.</summary>
    private void CloseFocusedPane(object? sender)
    {
        if (LocatePane(sender) is not { } located)
        {
            return;
        }

        ClosePaneOrTab(located.Tab, located.Container, sender as TerminalView ?? located.Container.ActiveView);
    }

    private void ClosePaneOrTab(TerminalTab tab, SplitContainer container, TerminalView? view)
    {
        if (view is null)
        {
            return;
        }

        if (container.Count <= 1)
        {
            CloseTab(tab);
            return;
        }

        ClosePane(container, view);
        container.ActiveView?.FocusTerminal();
    }

    private void ClosePane(SplitContainer container, TerminalView view)
    {
        view.AcceleratorPressed -= OnAcceleratorPressed;
        _paneSources.Remove(view);
        container.Remove(view);
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

    private void SplitSame_Click(object sender, RoutedEventArgs e) =>
        AddSplitTo(SelectedTerminalTab(), sameConnection: true);

    private void SplitNew_Click(object sender, RoutedEventArgs e) =>
        AddSplitTo(SelectedTerminalTab(), sameConnection: false);

    /// <summary>The selected tab paired with its split holder, or null if it is a page tab.</summary>
    private (TerminalTab Tab, SplitContainer Container)? SelectedTerminalTab()
    {
        var tab = Tabs.FirstOrDefault(t => t.IsSelected);
        return tab?.Panes is { } container ? (tab, container) : null;
    }

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

    // ================= tab card editor =================

    private TerminalTab? _cardEditTab;

    /// <summary>The chat whose card is being edited, when the editor was opened for one.</summary>
    private ChatSession? _cardEditChat;

    /// <summary>Opens the card editor for a chat straight from the sidebar list.</summary>
    /// <remarks>
    /// The same popup a tab's right-click opens, so a chat is dressed the same way whether or
    /// not it happens to be open at the time.
    /// </remarks>
    private void ChatList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatSession chat })
        {
            return;
        }

        OpenCardEditor(chat.Card, (UIElement)sender, tab: null, chat);
        e.Handled = true;
    }

    /// <summary>
    /// Shows the card editor over something, for whichever of a tab and a chat it belongs to.
    /// </summary>
    /// <remarks>
    /// One entry point for all three ways in — a tab's right-click, the sidebar list, and F2 —
    /// so a chat is dressed and named the same way whichever route was taken.
    /// </remarks>
    private void OpenCardEditor(TabCardStyle card, UIElement target, TerminalTab? tab, ChatSession? chat)
    {
        _cardEditTab = tab;
        _cardEditChat = chat;

        var forChat = chat is not null ? Visibility.Visible : Visibility.Collapsed;

        CardAvatarRow.Visibility = forChat;
        CardAvatarBox.Text = chat?.AvatarPath ?? string.Empty;

        TabCardPopup.DataContext = card;
        TabCardPopup.PlacementTarget = target;
        TabCardPopup.IsOpen = true;
    }

    private void CardImageBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (TabCardPopup.DataContext is not TabCardStyle card)
        {
            return;
        }

        if (BrowseForImage() is { } chosen)
        {
            card.ImagePath = chosen;
        }
    }

    private void CardImageClear_Click(object sender, RoutedEventArgs e)
    {
        if (TabCardPopup.DataContext is TabCardStyle card)
        {
            card.ImagePath = string.Empty;
        }
    }

    /// <summary>
    /// Asks for a picture, holding the card editor open while the file dialog is up.
    /// </summary>
    /// <remarks>
    /// The editor closes on any click outside itself, and every click in the file dialog is
    /// outside it, so without pinning it the editor would vanish the moment browsing began.
    /// </remarks>
    private string? BrowseForImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
            CheckFileExists = true,
        };

        TabCardPopup.StaysOpen = true;
        try
        {
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        finally
        {
            TabCardPopup.StaysOpen = false;
        }
    }

    private void CardAvatarBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseForImage() is { } chosen)
        {
            CardAvatarBox.Text = chosen;
        }
    }

    private void CardAvatarClear_Click(object sender, RoutedEventArgs e) =>
        CardAvatarBox.Text = string.Empty;

    private void Tab_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TerminalTab tab })
        {
            return;
        }

        SelectTab(tab);
        tab.Card ??= new TabCardStyle();

        OpenCardEditor(tab.Card, (UIElement)sender, tab, tab.Chat);
        e.Handled = true;
    }

    /// <summary>Opens the colour picker over the tab-card swatch, holding the card popup open.</summary>
    private void TabCardSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabCardStyle card })
        {
            return;
        }

        // The card popup closes on any click outside itself; the picker is a separate popup, so
        // pin the card popup open while the picker is up and release it when the picker closes.
        var initial = string.IsNullOrWhiteSpace(card.Color) ? "#2A2A2A" : card.Color;
        TabCardPopup.StaysOpen = true;
        Controls.ColorPickerPopup.Show(
            (UIElement)sender,
            initial,
            hex => card.Color = hex,
            onClosed: () => TabCardPopup.StaysOpen = false);
        e.Handled = true;
    }

    private void TabCardReset_Click(object sender, RoutedEventArgs e)
    {
        if (TabCardPopup.DataContext is TabCardStyle card)
        {
            card.Color = string.Empty;
            card.Opacity = 1.0;
            card.Blur = 0;
            card.ImagePath = string.Empty;
        }
    }

    private void TabCardPopup_Closed(object sender, EventArgs e)
    {
        // A right-click tweak to a saved session's card is written straight back to disk.
        if (_cardEditTab?.Session is not null)
        {
            _store.Save();
        }

        if (_cardEditChat is { } chat)
        {
            chat.AvatarPath = CardAvatarBox.Text.Trim();
            ChatStore.Save(chat);

            // The open tab, if there is one, is showing the old picture until it is told.
            if (Tabs.FirstOrDefault(t => t.Chat?.Id == chat.Id)?.Content is AgentChatView view)
            {
                view.RefreshAvatar();
            }
        }

        _cardEditTab = null;
        _cardEditChat = null;
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

    // ================= "save this SSH?" from a typed console command =================

    /// <summary>
    /// The terminal noticed the user run an <c>ssh …</c> command at a local shell. Offer to keep
    /// it as a saved session, unless that host is already in the sidebar.
    /// </summary>
    private void OnSshCommandTyped(SshSession session)
    {
        if (IsAlreadySaved(session))
        {
            return;
        }

        _toastSession = session;
        ShowToast(
            LocalizationService.T("L_SaveThisSession"),
            LocalizationService.T("L_Save"),
            SaveTypedSession);
    }

    private bool IsAlreadySaved(SshSession candidate) =>
        _store.Sessions.Any(existing =>
            string.Equals(existing.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
            && existing.Port == candidate.Port
            && string.Equals(existing.Username, candidate.Username, StringComparison.OrdinalIgnoreCase));

    private void SaveTypedSession()
    {
        var session = _toastSession;
        _toastSession = null;
        if (session is null)
        {
            return;
        }

        if (!IsAlreadySaved(session))
        {
            _store.Sessions.Add(session);
            _store.Save();
            SessionList.SelectedItem = session;
        }

        ShowToast(
            string.Format(LocalizationService.T("L_SaveToastSaved"), session.Name),
            actionLabel: null,
            onAction: null);
    }

    // ================= bottom-left toast =================

    private SshSession? _toastSession;
    private Action? _toastAction;

    // Bumped whenever the toast is shown, hovered or hidden, so a fade that was already running
    // knows it is stale and does not hide a toast the user has since brought back.
    private int _toastGeneration;

    private void ShowToast(string text, string? actionLabel, Action? onAction)
    {
        SaveToastText.Text = text;
        _toastAction = onAction;

        if (actionLabel is null)
        {
            SaveToastButtons.Visibility = Visibility.Collapsed;
        }
        else
        {
            SaveToastButtons.Visibility = Visibility.Visible;
            SaveToastAction.Content = actionLabel;
        }

        SaveToast.Visibility = Visibility.Visible;
        BeginToastFade();
    }

    private void BeginToastFade()
    {
        var generation = ++_toastGeneration;

        SaveToast.BeginAnimation(OpacityProperty, null);
        SaveToast.Opacity = 1;

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromSeconds(10),
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, _) =>
        {
            // Only fold it away if this very fade ran to the end untouched.
            if (generation == _toastGeneration)
            {
                HideToast();
            }
        };

        SaveToast.BeginAnimation(OpacityProperty, fade);
    }

    private void SaveToast_MouseEnter(object sender, MouseEventArgs e)
    {
        _toastGeneration++;
        SaveToast.BeginAnimation(OpacityProperty, null);
        SaveToast.Opacity = 1;
    }

    private void SaveToast_MouseLeave(object sender, MouseEventArgs e)
    {
        if (SaveToast.Visibility == Visibility.Visible)
        {
            BeginToastFade();
        }
    }

    private void SaveToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        _toastAction = null;
        HideToast();
        action?.Invoke();
    }

    private void SaveToastDismiss_Click(object sender, RoutedEventArgs e)
    {
        _toastSession = null;
        HideToast();
    }

    private void HideToast()
    {
        _toastGeneration++;
        SaveToast.BeginAnimation(OpacityProperty, null);
        SaveToast.Opacity = 1;
        SaveToast.Visibility = Visibility.Collapsed;
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

    // The newest frame, copied out of the capture source's own buffer, plus the gate that keeps
    // the capture thread and the UI thread off it at the same time.
    private readonly object _frameGate = new();
    private byte[]? _framePixels;
    private WallpaperCapture.DesktopFrame _frame;
    private bool _framePosted;

    /// <summary>
    /// Takes one captured frame, on the capture thread, and asks the UI thread to draw it.
    /// </summary>
    /// <remarks>
    /// The frame is copied here rather than handed on as it stands, because a capture source's
    /// buffer only belongs to the handler for the length of the call: both sources rotate two
    /// buffers and rewrite each one a frame or two later, and the engine source additionally
    /// swaps red and blue in place over it, since Wallpaper Engine presents RGBA. Drawing that
    /// buffer later — which is what posting it to the dispatcher did — meant the UI thread read
    /// it while the capture thread was rewriting and re-swizzling it, so part of the picture
    /// came out with red and blue exchanged: the blue flicker over the live wallpaper. At the
    /// top of the frame-rate range the two threads overlapped nearly every frame.
    /// </remarks>
    private void OnWallpaperFrame(WallpaperCapture.DesktopFrame frame)
    {
        bool post;

        lock (_frameGate)
        {
            var length = frame.Stride * frame.Height;
            if (length <= 0 || length > frame.Pixels.Length)
            {
                return;
            }

            if (_framePixels is null || _framePixels.Length != length)
            {
                _framePixels = new byte[length];
            }

            Buffer.BlockCopy(frame.Pixels, 0, _framePixels, 0, length);
            _frame = frame with { Pixels = _framePixels };

            // One pending draw at a time. Capture can outrun the UI thread, and at the top of the
            // frame-rate range it does, so queueing an operation per frame would only build a
            // backlog of stale pictures in front of whatever the user is actually waiting on.
            post = !_framePosted;
            _framePosted = true;
        }

        if (post)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, DrawLatestFrame);
        }
    }

    /// <summary>Draws the newest frame received since the last time this ran.</summary>
    private void DrawLatestFrame()
    {
        lock (_frameGate)
        {
            _framePosted = false;

            if (_framePixels is null)
            {
                return;
            }

            var frame = _frame;
            _desktopOriginX = frame.OriginX;
            _desktopOriginY = frame.OriginY;

            // The frame spans the primary screen, whose width in DIPs WPF already knows. Pixels
            // divided by that is the frame's true scale, whether it arrived full-size from the
            // desktop capture or downscaled from the engine hook.
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            _frameScale = screenWidth > 1 ? frame.Width / screenWidth : 1;

            WindowBackdrop.PushFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride, _frameScale);

            // The settings preview, if open, draws the same frame — no second capture needed.
            // Inside the gate as well: it reads the same buffer.
            LiveWallpaperBroker.Publish(frame);
        }

        UpdateLiveLayout();
    }

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

    // ================= tray & elevation =================

    private TrayManager? _tray;

    private void SetupTray()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        _tray = new TrayManager(exe, offerElevation: !Elevation.IsElevated);
        _tray.ShowRequested += RestoreFromTray;
        _tray.RestartElevatedRequested += Elevation.RestartElevated;
        _tray.ExitRequested += Close;
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_tray is null)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        _tray.ShowIcon();
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _tray?.HideIcon();
    }

    /// <summary>
    /// Gains administrator rights without going away and coming back.
    /// </summary>
    /// <remarks>
    /// Windows fixes a process's elevation when it starts, so this window can never become
    /// administrator itself. What it can do is start an elevated helper and send it the work that
    /// needs the rights — the tabs, connections and scrollback all stay exactly where they are.
    /// </remarks>
    private async void Elevate_Click(object sender, RoutedEventArgs e)
    {
        ShellPopup.IsOpen = false;

        var refused = await ElevatedHost.StartAsync();

        ShowToast(
            refused ?? LocalizationService.T("L_ElevateReady"),
            actionLabel: null,
            onAction: null);

        UpdateElevationMenu();
    }

    /// <summary>Hides the offer once there is nothing left to gain by taking it.</summary>
    private void UpdateElevationMenu() =>
        ElevateMenuButton.Visibility = Elevation.IsElevated || ElevatedHost.IsRunning
            ? Visibility.Collapsed
            : Visibility.Visible;

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



