using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// The window's session store, reachable from code that is never handed it.
/// </summary>
/// <remarks>
/// The store belongs to the main window, which owns the sidebar it fills. A chat attachment names
/// a session by id and has to resolve it long after the picker has closed — from a background
/// turn, in a service with no view behind it — so the one live store is published here rather
/// than threaded through every layer between. Set once at startup; there is only ever one.
/// </remarks>
public static class SessionCatalog
{
    public static SessionStore? Store { get; set; }

    public static IReadOnlyList<SshSession> All =>
        Store is null ? [] : [.. Store.Sessions];

    /// <summary>The session an attachment refers to, or null once it has been deleted.</summary>
    public static SshSession? Find(string attachment)
    {
        const string Scheme = "ssh-session:";

        if (!attachment.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(attachment[Scheme.Length..], out var id))
        {
            return null;
        }

        return Store?.Sessions.FirstOrDefault(session => session.Id == id);
    }

    /// <summary>How a session is written when it is attached to a message.</summary>
    public static string Reference(SshSession session) => $"ssh-session:{session.Id}";
}
