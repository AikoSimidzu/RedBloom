using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RedBloom.Services;

/// <summary>
/// What machine the agent is running on, and the standing instructions that tell it how to work
/// here — the environment preamble that leads every agent's system prompt.
/// </summary>
/// <remarks>
/// A frontier model asked to act on a computer performs far better when it is told what computer,
/// what shell, where it is standing, and which tools it has — the same framing a coding agent
/// carries. Without it the model guesses, and the guesses are where the "why is it being dense"
/// answers come from. The machine facts are read once and cached; only the working directory
/// changes between turns, so the preamble is rebuilt cheaply from the cached description.
/// </remarks>
public static class SystemInfo
{
    /// <summary>The one-line machine description, read from the registry once.</summary>
    public static string Os => _os.Value;

    /// <summary>The shell <c>run_command</c> runs through.</summary>
    public static string Shell =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static readonly Lazy<string> _os = new(DescribeOs);

    private static string DescribeOs()
    {
        var caption = "Windows";
        var display = string.Empty;
        var build = string.Empty;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key is not null)
            {
                caption = key.GetValue("ProductName") as string ?? caption;
                display = key.GetValue("DisplayVersion") as string ?? string.Empty;

                var currentBuild = key.GetValue("CurrentBuildNumber") as string ?? string.Empty;
                var ubr = key.GetValue("UBR");
                build = ubr is int rev && currentBuild.Length > 0 ? $"{currentBuild}.{rev}" : currentBuild;

                // The registry still says "Windows 10" on eleven; the build number is the honest
                // line between them, so the name is corrected from it.
                if (int.TryParse(currentBuild, out var n) && n >= 22000 && caption.Contains("Windows 10"))
                {
                    caption = caption.Replace("Windows 10", "Windows 11");
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No registry access; the OSVersion fallback below still gives a build number.
        }

        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (build.Length == 0)
        {
            build = Environment.OSVersion.Version.Build.ToString();
        }

        var version = display.Length > 0 ? $"{display}, build {build}" : $"build {build}";
        return $"{caption} ({version}), {arch}";
    }

    /// <summary>Whether PowerShell is on this machine, so the preamble can point the model at it.</summary>
    private static readonly Lazy<string> _powershell = new(DetectPowerShell);

    private static string DetectPowerShell()
    {
        // pwsh (7+) is preferred when present; Windows PowerShell 5.1 is always there.
        if (OnPath("pwsh.exe"))
        {
            return "PowerShell 7+ (pwsh) and Windows PowerShell 5.1";
        }

        var win = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        return File.Exists(win) ? "Windows PowerShell 5.1 (powershell)" : "not detected";
    }

    private static bool OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir.Trim(), exe)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; skip it.
            }
        }

        return false;
    }

    /// <summary>
    /// The standing environment block placed at the head of an agent's system prompt: what machine
    /// this is, where it is standing, and how to use its tools well.
    /// </summary>
    /// <param name="workspace">This chat's own working folder, the default working directory.</param>
    public static string Preamble(string workspace)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "You are acting as an autonomous agent on the user's own Windows computer, through "
            + "RedBloom (a custom terminal with an AI chat). Be direct: take the steps the task "
            + "needs and report what you did, rather than narrating what you are about to do or "
            + "asking permission for ordinary read-only work. Reference files as full paths.");
        sb.AppendLine();
        sb.AppendLine("Environment:");
        sb.AppendLine($"- OS: {Os}");
        sb.AppendLine($"- Terminal: RedBloom");
        sb.AppendLine($"- Shell for run_command: cmd.exe (non-interactive). PowerShell: {_powershell.Value}.");
        sb.AppendLine($"- Working directory: {workspace}");
        sb.AppendLine($"- Logical CPUs: {Environment.ProcessorCount}");
        sb.AppendLine();
        sb.AppendLine(
            "run_command runs one cmd.exe command and returns its output. The working directory is "
            + "remembered between your calls in this chat, so a `cd` you run persists to the next "
            + "command — but environment variables set in one call do not, and each call is still a "
            + "fresh shell, so chain dependent steps in one command with `&&`. Output over about "
            + "20000 characters is cut, so filter with findstr rather than dumping whole files. Do "
            + "NOT start a GUI app with run_command — the command blocks until the app is closed, so "
            + "you would just sit waiting. Launch apps with the manage_window tool (action "
            + "\"launch\"), which returns while the app keeps running. You cannot see the screen on "
            + "your own, so to check or test a GUI app use manage_window action \"screenshot\" — it "
            + "returns a picture of the window (or the whole screen) for you to look at. To actually "
            + "operate an app, use control_mouse to move and click and type_keys to type text or "
            + "press keys (click into a field first, then type) — take a whole-screen screenshot "
            + "first, read the point off it, act, then screenshot again to see what happened. A good "
            + "loop is launch → screenshot → click/type → screenshot to confirm → close.");
        sb.AppendLine();
        sb.AppendLine(
            "For files, use the file tools rather than the shell — they are exact and avoid the "
            + "quoting and encoding traps of echo/Set-Content one-liners:");
        sb.AppendLine("- read_file: read a file, optionally a line range. Not truncated the way `type` is.");
        sb.AppendLine("- write_file: create a file or replace it whole.");
        sb.AppendLine("- edit_file: replace an exact piece of a file (old text -> new text).");
        sb.AppendLine("- list_dir: list a folder's contents.");
        sb.Append(
            "Relative paths in these tools and in run_command resolve against the working directory "
            + "above. Do the work in that folder unless the user points you elsewhere.");

        return sb.ToString();
    }

    /// <summary>
    /// The preamble when the chat is working over SSH: the same tools, but every one of them acts
    /// on the remote machine across a single live connection, not on this computer.
    /// </summary>
    public static string RemotePreamble(string host, string user, string cwd)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $"You are acting as an autonomous agent on a REMOTE machine over SSH — {user}@{host} — "
            + "through RedBloom. run_command and the file tools all operate on that remote machine, "
            + "not on the local computer, across one connection that stays open between calls. Be "
            + "direct: do the work and report it.");
        sb.AppendLine();
        sb.AppendLine("Environment:");
        sb.AppendLine($"- Remote host: {host} (user {user})");
        sb.AppendLine("- Shell for run_command: the remote's POSIX shell (sh/bash). Commands run there.");
        sb.AppendLine($"- Working directory (on the remote): {cwd}");
        sb.AppendLine();
        sb.AppendLine(
            "run_command runs one command on the remote and returns its output. The working "
            + "directory persists between your calls, so a `cd` carries onward; environment "
            + "variables set in one call do not. Output is cut if very long, so filter with grep "
            + "rather than printing whole files.");
        sb.AppendLine();
        sb.AppendLine(
            "The file tools act on the remote machine over the same connection — use them instead of "
            + "cat/heredoc tricks:");
        sb.AppendLine("- read_file: read a remote file, optionally a line range.");
        sb.AppendLine("- write_file: create or replace a remote file.");
        sb.AppendLine("- edit_file: replace an exact piece of a remote file.");
        sb.AppendLine("- list_dir: list a remote folder.");
        sb.Append("Relative paths resolve against the remote working directory above.");

        return sb.ToString();
    }
}
