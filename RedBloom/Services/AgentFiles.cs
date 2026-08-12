using System.IO;

namespace RedBloom.Services;

/// <summary>
/// Files agents have produced or handed over, and the folder the file browser starts in.
/// </summary>
/// <remarks>
/// The "recently changed" list is what an agent has actually put in front of the user — a picture
/// it drew, a file it shared — rather than every file on disk that happens to be new. Held here,
/// process-wide, because a file can be produced in one chat and wanted from the browser while
/// another is open. Capped so it cannot grow without bound over a long session.
/// </remarks>
public static class AgentFiles
{
    private const int Limit = 200;

    private static readonly object Gate = new();
    private static readonly List<string> Recent_ = [];

    /// <summary>Where "all files" starts browsing. The user's own folder unless changed.</summary>
    public static string Root { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Raised when the recent list changes, so an open browser can refresh.</summary>
    public static event Action? Changed;

    /// <summary>Records that an agent produced or handed over a file, newest last.</summary>
    public static void Touched(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return;
        }

        lock (Gate)
        {
            Recent_.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            Recent_.Add(full);

            while (Recent_.Count > Limit)
            {
                Recent_.RemoveAt(0);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The agent-touched files that still exist, most recent first.</summary>
    public static IReadOnlyList<string> Recent()
    {
        lock (Gate)
        {
            return [.. Enumerable.Reverse(Recent_).Where(File.Exists)];
        }
    }
}
