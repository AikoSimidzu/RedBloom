using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RedBloom.Models;

/// <summary>How a room decides who speaks after a message.</summary>
public enum RoomPolicy
{
    /// <summary>Every participant answers each message, in the order they are listed.</summary>
    All,

    /// <summary>One participant answers per message, cycling through the list.</summary>
    RoundRobin,

    /// <summary>Only participants named with an @ in the last message answer.</summary>
    Mention,

    /// <summary>A moderator agent is asked who should speak next, until it says nobody.</summary>
    Moderator,
}

/// <summary>
/// A group conversation with several agents at once.
/// </summary>
/// <remarks>
/// Kept apart from a one-agent <see cref="ChatSession"/> because the two differ in what they hold,
/// not only in degree: a room has a cast rather than a single agent, and a rule for who takes the
/// floor. The turns are still <see cref="ChatTurn"/>s — each now carrying who spoke — so the same
/// page renders both. Stored as its own file under <c>%APPDATA%\RedBloom\rooms</c>, the same way a
/// chat is, for the same reason: it grows without limit and is written after every turn.
/// </remarks>
public sealed class ChatRoom : INotifyPropertyChanged
{
    private string _title = "Room";
    private RoomPolicy _policy = RoomPolicy.Mention;
    private int _rotation;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Which project this room is filed under, by <see cref="Project.Id"/>. Empty when loose.</summary>
    public string ProjectId { get; set; } = string.Empty;

    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>The agents in the room, by <see cref="AiAgent.Id"/>, in speaking order.</summary>
    public List<string> ParticipantIds { get; set; } = [];

    /// <summary>Which agent moderates, when the policy is <see cref="RoomPolicy.Moderator"/>.</summary>
    public string ModeratorId { get; set; } = string.Empty;

    public RoomPolicy Policy { get => _policy; set => Set(ref _policy, value); }

    /// <summary>
    /// Whether the agents in this room may run commands on the machine.
    /// </summary>
    /// <remarks>
    /// A permission for the whole room rather than a per-agent flag here: a room is a place the
    /// user opens deliberately, and turning this on says the cast in it is trusted to act on the
    /// machine. Off by default, and <see cref="AskBeforeRun"/> still stands between a decision and
    /// the machine.
    /// </remarks>
    public bool AllowCommands { get; set; }

    /// <summary>Whether each command is shown for approval before it runs. On by default.</summary>
    public bool AskBeforeRun { get; set; } = true;

    /// <summary>Where round-robin left off, so reopening the room does not restart the rotation.</summary>
    public int Rotation { get => _rotation; set => _rotation = value; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ChatTurn> Turns { get; set; } = [];

    /// <summary>This room's task list, shown in its header and shareable with the cast.</summary>
    public List<TaskItem> Tasks { get; set; } = [];

    /// <summary>This room's own look in the tab strip.</summary>
    public TabCardStyle Card { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => Turns.Count == 0;

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var when = UpdatedAt.Date == DateTime.Today
                ? UpdatedAt.ToString("HH:mm")
                : UpdatedAt.ToString("d MMM");

            var count = ParticipantIds.Count;
            return $"{when} · {count} {(count == 1 ? "agent" : "agents")}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Changed;

    public void Touch()
    {
        UpdatedAt = DateTime.Now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        Changed?.Invoke();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        Changed?.Invoke();
    }
}
