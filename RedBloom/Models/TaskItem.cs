using System.Text.Json.Serialization;

namespace RedBloom.Models;

/// <summary>Where a task stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState
{
    /// <summary>Not begun.</summary>
    NotStarted,

    /// <summary>Being worked on now.</summary>
    InProgress,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Done once, but sent back for changes.</summary>
    NeedsRework,

    /// <summary>Written, waiting on its tests.</summary>
    Tests,
}

/// <summary>
/// One task on a chat's or a room's list — a name, a description and where it stands, plus who is
/// on it in a room.
/// </summary>
/// <remarks>
/// Kept with the chat or room it belongs to, so it travels with the conversation and can be handed
/// to the agents whole or one task at a time. The assignee is a room idea — a name from the cast —
/// and is left empty in a one-to-one chat, where there is only ever the one agent.
/// </remarks>
public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public TaskState State { get; set; } = TaskState.NotStarted;

    /// <summary>The name of the participant this task is assigned to, in a room. Empty otherwise.</summary>
    public string Assignee { get; set; } = string.Empty;
}
