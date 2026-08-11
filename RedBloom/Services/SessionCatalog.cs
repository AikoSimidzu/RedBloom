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

    private const string Scheme = "ssh-session:";

    /// <summary>Marks an attachment whose password the user chose to send along with it.</summary>
    private const string WithSecret = "#secret";

    /// <summary>The session an attachment refers to, or null once it has been deleted.</summary>
    public static SshSession? Find(string attachment)
    {
        if (!attachment.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = attachment[Scheme.Length..];

        if (id.EndsWith(WithSecret, StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^WithSecret.Length];
        }

        return Guid.TryParse(id, out var parsed)
            ? Store?.Sessions.FirstOrDefault(session => session.Id == parsed)
            : null;
    }

    /// <summary>
    /// Whether this attachment was made with the password included.
    /// </summary>
    /// <remarks>
    /// Carried in the reference rather than read from the session, so the choice belongs to the
    /// message it was attached to: sending the password once does not send it in every later
    /// chat that happens to name the same machine.
    /// </remarks>
    public static bool CarriesSecret(string attachment) =>
        attachment.EndsWith(WithSecret, StringComparison.OrdinalIgnoreCase);

    /// <summary>A session by its plain id, as an agent's tunnel setting stores it.</summary>
    public static SshSession? ById(string id) =>
        Guid.TryParse(id, out var parsed)
            ? Store?.Sessions.FirstOrDefault(session => session.Id == parsed)
            : null;

    /// <summary>How a session is written when it is attached to a message.</summary>
    public static string Reference(SshSession session, bool withSecret) =>
        Scheme + session.Id + (withSecret ? WithSecret : string.Empty);
}
