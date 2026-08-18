namespace RedBloom.Models;

/// <summary>Where a project source comes from — how it was added, which shapes how it is tracked.</summary>
public enum SourceKind
{
    /// <summary>A folder on disk the user pointed at.</summary>
    Local,

    /// <summary>A Visual Studio solution or project, discovered or pointed at.</summary>
    VisualStudio,

    /// <summary>A GitHub repository, linked from the user's account (cloned or not).</summary>
    GitHub,
}

/// <summary>
/// A source folder or repository linked to a project and tracked alongside it — the sources are the
/// actual code the project is about, separate from the project's own workspace.
/// </summary>
public sealed class ProjectSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public SourceKind Kind { get; set; } = SourceKind.Local;

    /// <summary>A short name for the source — the folder, solution or repo name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder on disk this source lives in. Empty for a GitHub repo not cloned yet.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>For a GitHub source: the repository, e.g. <c>owner/name</c>, and its web URL.</summary>
    public string Repo { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; } = DateTime.Now;
}
