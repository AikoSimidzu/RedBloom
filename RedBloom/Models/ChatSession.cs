using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RedBloom.Models;

/// <summary>One saved turn of a conversation.</summary>
public sealed class ChatTurn
{
    public string Role { get; set; } = "user";

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Who said this, in a room where several agents talk. Empty in an ordinary one-agent chat,
    /// where the role alone says whether it was the user or the agent.
    /// </summary>
    public string Speaker { get; set; } = string.Empty;

    /// <summary>A picture this turn is, by path, when an image agent drew it. Empty otherwise.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>
    /// A command the agent ran, when <see cref="Role"/> is <c>command</c>. Kept in the transcript so
    /// the commands do not vanish when the chat is reopened.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>What the command printed.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// The git diff the command produced, as unified diff text, or empty when it changed nothing —
    /// or when it did not run inside a git repository. Shown with per-line +/- and a jump to the file.
    /// </summary>
    public string Diff { get; set; } = string.Empty;

    /// <summary>
    /// What was attached to this message, by path.
    /// </summary>
    /// <remarks>
    /// Attachments belong to the message they were sent with rather than to the chat: that is
    /// where they were meant, and it keeps the transcript a record of what was actually said
    /// and shown at the time.
    /// </remarks>
    public List<string> Attachments { get; set; } = [];
}

/// <summary>
/// A conversation with one agent, kept between runs.
/// </summary>
/// <remarks>
/// Stored per chat rather than inside the settings file: a conversation grows without limit and
/// is written after every turn, which is the opposite of what settings want. Each chat is its
/// own file under <c>%APPDATA%\RedBloom\chats</c>, so a corrupted or enormous one costs only
/// itself.
/// </remarks>
public sealed class ChatSession : INotifyPropertyChanged
{
    private string _title = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Which agent this belongs to, by <see cref="AiAgent.Id"/>.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Taken from the first thing the user asked, until they rename it.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>
    /// Which model answers here, when it should not be the agent's own. Empty follows the agent.
    /// </summary>
    /// <remarks>
    /// Per chat rather than per agent because the choice belongs to the conversation: switching
    /// to a bigger model for one hard question should not quietly re-point every other chat, and
    /// the endpoint and key — the parts that are actually configuration — stay the agent's.
    /// </remarks>
    public string Model { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ChatTurn> Turns { get; set; } = [];

    /// <summary>This chat's task list, shown in its header and shareable with the agent.</summary>
    public List<TaskItem> Tasks { get; set; } = [];

    /// <summary>This chat's own look in the tab strip, edited the same way a session's is.</summary>
    public TabCardStyle Card { get; set; } = new();

    /// <summary>
    /// What the agent is called here, overriding its own name. Empty follows the agent.
    /// </summary>
    /// <remarks>
    /// Per chat for the same reason the avatar is: one agent plays several parts, and a name is
    /// the fastest way to tell which conversation is which when both are open.
    /// </remarks>
    public string BotName { get; set; } = string.Empty;

    /// <summary>
    /// A picture for this conversation alone, overriding the agent's. Empty follows the agent.
    /// </summary>
    /// <remarks>
    /// Per chat as well as per agent because one agent is often several things at once — a
    /// chat about the router and a chat about a build read more easily when they do not look
    /// identical.
    /// </remarks>
    public string AvatarPath { get; set; } = string.Empty;

    /// <summary>
    /// Files and folders the agent should see from the start of this chat.
    /// </summary>
    /// <remarks>
    /// Paths only. Their contents are read fresh on every send, so an edit between turns
    /// reaches the model, and a large file never ends up copied into the saved conversation.
    /// </remarks>
    public List<string> Attachments { get; set; } = [];

    /// <summary>Nothing has been said yet, so the chat is not worth listing or saving.</summary>
    [JsonIgnore]
    public bool IsEmpty => Turns.Count == 0 && Tasks.Count == 0;

    /// <summary>What the list shows under the title.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var when = UpdatedAt.Date == DateTime.Today
                ? UpdatedAt.ToString("HH:mm")
                : UpdatedAt.ToString("d MMM");

            var count = Turns.Count(t => t.Role == "user");
            return $"{when} · {count} {(count == 1 ? "message" : "messages")}";
        }
    }

    /// <summary>Builds a title out of the first question, short enough for the list.</summary>
    public static string TitleFrom(string question)
    {
        var text = question.Trim().ReplaceLineEndings(" ");

        if (text.Length <= 44)
        {
            return text.Length > 0 ? text : "New chat";
        }

        // Cut on a word boundary when there is one nearby, so the title does not end mid-word.
        var cut = text.LastIndexOf(' ', 44);
        return text[..(cut > 20 ? cut : 44)] + "…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on any change worth writing to disk.</summary>
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
