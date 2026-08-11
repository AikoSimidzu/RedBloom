using System.IO;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Claude Code, the command-line tool, offered as an agent.
/// </summary>
/// <remarks>
/// Worth having beside the endpoint-based agents because it is a different thing: it signs in
/// through the website rather than with an API key, it brings its own tools and project
/// awareness, and it bills against whatever plan the user already has. Nothing here reimplements
/// any of that — the installed program is run, and its streamed output is read.
/// </remarks>
public static class ClaudeCli
{
    /// <summary>The id this agent is filed under, so its chats survive restarts.</summary>
    public const string AgentId = "claude-cli";

    private static readonly Lazy<string?> Found = new(Locate);

    /// <summary>Where the tool is, or null when it is not installed.</summary>
    public static string? Executable => Found.Value;

    public static bool IsInstalled => Executable is not null;

    /// <summary>The agent that runs it.</summary>
    public static AiAgent Agent => new()
    {
        Id = AgentId,
        Name = "Claude CLI",
        Provider = AiProvider.ClaudeCli,

        // Left empty on purpose: the tool picks whatever the signed-in plan offers, and naming a
        // model here would override that with a guess.
        Model = string.Empty,
        ContextWindow = 200000,
        Thinking = false,

        // It signs in through the browser and keeps its own credentials; there is no key to hold.
        ApiKey = "cli",
    };

    /// <summary>
    /// The command that signs in, to be run in a terminal where the user can see it.
    /// </summary>
    /// <remarks>
    /// Sign-in opens a browser and waits for the answer, so it needs a real terminal rather than
    /// a captured pipe — which is why this is handed back as a command to run in a tab instead
    /// of being driven from inside the app.
    /// </remarks>
    public static string LoginCommand => $"\"{Executable}\" /login";

    private static string? Locate()
    {
        var candidates = new List<string>();

        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            foreach (var folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // The npm shim is a .cmd on Windows; the .ps1 beside it cannot be started
                // directly, so it is not looked for.
                candidates.Add(Path.Combine(folder.Trim('"'), "claude.cmd"));
                candidates.Add(Path.Combine(folder.Trim('"'), "claude.exe"));
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"));
        candidates.Add(Path.Combine(profile, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(profile, ".claude", "local", "claude.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; the rest are still worth trying.
            }
        }

        return null;
    }
}
