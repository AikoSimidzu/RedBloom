using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        _room.Title = string.IsNullOrWhiteSpace(NameBox.Text) ? LocalizationService.T("L_RoomNew") : NameBox.Text.Trim();
        _room.ParticipantIds = chosen;
        _room.Policy = PolicyOf(PolicyBox.SelectedIndex);
        _room.ModeratorId = (ModeratorBox.SelectedItem as AiAgent)?.Id
            ?? chosen.FirstOrDefault() ?? string.Empty;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>One agent as an option in the participant list, with its chosen state.</summary>
    private sealed class Pick(AiAgent agent) : INotifyPropertyChanged
    {
        private bool _chosen;

        public AiAgent Agent { get; } = agent;

        public string Name => Agent.Name;

        public bool Chosen
        {
            get => _chosen;
            set
            {
                _chosen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chosen)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
