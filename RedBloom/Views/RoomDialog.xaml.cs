using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>Creating or editing a room: its name, who is in it, and how the floor passes.</summary>
public partial class RoomDialog : Window
{
    private readonly ChatRoom _room;
    private readonly List<Pick> _picks;

    private RoomDialog(ChatRoom room)
    {
        InitializeComponent();

        _room = room;
        NameBox.Text = room.Title;

        _picks =
        [
            .. ThemeService.Settings.Agents.Select(agent => new Pick(agent)
            {
                Chosen = room.ParticipantIds.Contains(agent.Id),
            }),
        ];

        AgentsList.ItemsSource = _picks;
        PolicyBox.SelectedIndex = IndexOf(room.Policy);

        RefreshModerators();
        ModeratorBox.SelectedItem = _picks.FirstOrDefault(p => p.Agent.Id == room.ModeratorId)?.Agent;
        RefreshPolicyUi();
    }

    /// <summary>Shows the dialog for a room; true when the user chose to open it.</summary>
    public static bool Edit(Window owner, ChatRoom room)
    {
        var dialog = new RoomDialog(room) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private static int IndexOf(RoomPolicy policy) => policy switch
    {
        RoomPolicy.RoundRobin => 1,
        RoomPolicy.All => 2,
        RoomPolicy.Moderator => 3,
        _ => 0,
    };

    private static RoomPolicy PolicyOf(int index) => index switch
    {
        1 => RoomPolicy.RoundRobin,
        2 => RoomPolicy.All,
        3 => RoomPolicy.Moderator,
        _ => RoomPolicy.Mention,
    };

    private void Policy_Changed(object sender, SelectionChangedEventArgs e) => RefreshPolicyUi();

    private void RefreshPolicyUi()
    {
        var policy = PolicyOf(PolicyBox.SelectedIndex);

        ModeratorRow.Visibility = policy == RoomPolicy.Moderator ? Visibility.Visible : Visibility.Collapsed;

        if (policy == RoomPolicy.Moderator)
        {
            var kept = ModeratorBox.SelectedItem as AiAgent;
            RefreshModerators();
            ModeratorBox.SelectedItem = kept ?? (ModeratorBox.ItemsSource as IEnumerable<AiAgent>)?.FirstOrDefault();
        }

        if (PolicyNote is not null)
        {
            PolicyNote.Text = LocalizationService.T(policy switch
            {
                RoomPolicy.All => "L_RoomPolicyAllNote",
                RoomPolicy.RoundRobin => "L_RoomPolicyRoundRobinNote",
                RoomPolicy.Moderator => "L_RoomPolicyModeratorNote",
                _ => "L_RoomPolicyMentionNote",
            });
        }
    }

    private void RefreshModerators() =>
        ModeratorBox.ItemsSource = _picks.Where(p => p.Chosen).Select(p => p.Agent).ToList();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _picks.Where(p => p.Chosen).Select(p => p.Agent.Id).ToList();

        if (chosen.Count == 0)
        {
            MessageBox.Show(this, LocalizationService.T("L_RoomNeedAgents"),
                LocalizationService.T("L_RoomTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The identity fields — name, colour, avatar, model — are the agent's own, shared with its
        // one-to-one chat. Held while the dialog is open and written back only now, so a cancel
        // leaves the saved agents untouched. Persisted here because the room reads them straight off
        // the agents when it draws each reply.
        foreach (var pick in _picks)
        {
            pick.Apply();
        }

        ThemeService.Save();

        _room.Title = string.IsNullOrWhiteSpace(NameBox.Text) ? LocalizationService.T("L_RoomNew") : NameBox.Text.Trim();
        _room.ParticipantIds = chosen;
        _room.Policy = PolicyOf(PolicyBox.SelectedIndex);
        _room.ModeratorId = (ModeratorBox.SelectedItem as AiAgent)?.Id
            ?? chosen.FirstOrDefault() ?? string.Empty;

        DialogResult = true;
    }

    private void BrowseAvatar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Pick pick)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            pick.AvatarPath = dialog.FileName;
        }
    }

    private void ClearAvatar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Pick pick)
        {
            pick.AvatarPath = string.Empty;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// One agent as an option in the participant list, with its chosen state and an editable copy
    /// of its identity — name, nick colour, avatar and model.
    /// </summary>
    /// <remarks>
    /// The identity fields are held here rather than written straight through, so the participant
    /// list doubles as an editor without a cancel leaving half-applied changes on the shared agent.
    /// <see cref="Apply"/> commits them, called once when the room is saved.
    /// </remarks>
    private sealed class Pick : INotifyPropertyChanged
    {
        private bool _chosen;
        private string _name;
        private string _nameColor;
        private string _model;
        private string _avatarPath;

        public Pick(AiAgent agent)
        {
            Agent = agent;
            _name = agent.Name;
            _nameColor = agent.NameColor;
            _model = agent.Model;
            _avatarPath = agent.AvatarPath;
        }

        public AiAgent Agent { get; }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public string NameColor
        {
            get => _nameColor;
            set => Set(ref _nameColor, value);
        }

        public string Model
        {
            get => _model;
            set => Set(ref _model, value);
        }

        public string AvatarPath
        {
            get => _avatarPath;
            set
            {
                if (Set(ref _avatarPath, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarLabel)));
                }
            }
        }

        /// <summary>The chosen avatar's file name, or a placeholder when none is set.</summary>
        public string AvatarLabel => string.IsNullOrWhiteSpace(_avatarPath)
            ? LocalizationService.T("L_RoomAvatarNone")
            : Path.GetFileName(_avatarPath);

        /// <summary>A short tag naming what kind of agent this is, shown beside its name.</summary>
        public string Kind => Agent.Provider switch
        {
            AiProvider.ClaudeCli => "· CLI",
            AiProvider.ImageGen => "· " + LocalizationService.T("L_RoomKindImage"),
            _ when Agent.IsLocal || Agent.IsRemoteLocal => "· " + LocalizationService.T("L_RoomKindLocal"),
            _ => string.Empty,
        };

        /// <summary>
        /// Whether the model is the user's to type. The command-line tool and a discovered local
        /// model each answer as one fixed model, so there is nothing to choose.
        /// </summary>
        public bool ModelEditable => Agent.Provider != AiProvider.ClaudeCli && !Agent.IsLocal;

        public bool Chosen
        {
            get => _chosen;
            set => Set(ref _chosen, value);
        }

        /// <summary>Writes the edited identity back onto the agent.</summary>
        public void Apply()
        {
            Agent.Name = _name.Trim().Length == 0 ? Agent.Name : _name.Trim();
            Agent.NameColor = _nameColor.Trim();

            if (ModelEditable)
            {
                Agent.Model = _model.Trim();
            }

            Agent.AvatarPath = _avatarPath.Trim();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
