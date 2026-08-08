using System.IO;

namespace RedBloom.Terminal;

/// <summary>A launchable local shell, as offered by the new-tab dropdown.</summary>
public sealed class ShellProfile
{
    /// <summary>Segoe MDL2 "CommandPrompt"; shown in the dropdown and on the tab.</summary>
    public const string DefaultGlyph = "";

    public required string Name { get; init; }
    public required string Executable { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string? StartingDirectory { get; init; }

    public string Glyph { get; init; } = DefaultGlyph;

    public string BuildCommandLine() =>
        string.IsNullOrWhiteSpace(Arguments) ? $"\"{Executable}\"" : $"\"{Executable}\" {Arguments}";

    /// <summary>
    /// Enumerates the shells actually present on this machine. Command Prompt comes first
    /// because it is the default for a plain new tab.
    /// </summary>
    public static IReadOnlyList<ShellProfile> Discover()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var found = new List<ShellProfile>();

        var cmd = Path.Combine(system32, "cmd.exe");
        if (File.Exists(cmd))
        {
            found.Add(new ShellProfile
            {
                Name = "Command Prompt",
                Executable = cmd,
            });
        }

        var pwsh = ResolveOnPath("pwsh.exe")
                   ?? FirstExisting(
                       Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
                       Path.Combine(programFiles, "PowerShell", "6", "pwsh.exe"));
        if (pwsh is not null)
        {
            found.Add(new ShellProfile
            {
                Name = "PowerShell",
                Executable = pwsh,
                Arguments = "-NoLogo",
            });
        }

        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            found.Add(new ShellProfile
            {
                Name = "Windows PowerShell",
                Executable = powershell,
                Arguments = "-NoLogo",
            });
        }

        var gitBash = FirstExisting(
            Path.Combine(programFiles, "Git", "bin", "bash.exe"),
            Path.Combine(programFilesX86, "Git", "bin", "bash.exe"));
        if (gitBash is not null)
        {
            found.Add(new ShellProfile
            {
                Name = "Git Bash",
                Executable = gitBash,
                Arguments = "--login -i",
            });
        }

        var wsl = Path.Combine(system32, "wsl.exe");
        if (File.Exists(wsl))
        {
            found.Add(new ShellProfile
            {
                Name = "WSL",
                Executable = wsl,
            });
        }

        return found;
    }

    private static string? FirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);

    private static string? ResolveOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH entries can contain characters that are illegal in a path; skip them.
            }
        }

        return null;
    }
}
