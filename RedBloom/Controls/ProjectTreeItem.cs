using System.Collections.ObjectModel;
using RedBloom.Models;

namespace RedBloom.Controls;

/// <summary>A project as a row in the sidebar tree, with its chats and rooms as children.</summary>
public sealed class ProjectTreeItem
{
    public ProjectTreeItem(Project project) => Project = project;

    public Project Project { get; }

    public string Name => Project.Name;

    public ObservableCollection<ProjectTreeChild> Children { get; } = [];
}

/// <summary>A chat or room nested under a project in the sidebar tree.</summary>
public sealed class ProjectTreeChild
{
    public required string Title { get; init; }

    /// <summary>A Segoe MDL2 glyph telling a chat from a room at a glance.</summary>
    public required string Glyph { get; init; }

    public ChatSession? Chat { get; init; }

    public ChatRoom? Room { get; init; }
}
