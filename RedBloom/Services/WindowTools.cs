using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RedBloom.Services;

/// <summary>
/// Lets an agent see, focus and close the windows open on the user's machine — the apps it (or the
/// user) has launched — through a small set of Win32 calls, since the shell alone cannot bring a
/// window to the front or close one gracefully.
/// </summary>
public static class WindowTools
{
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;

    /// <summary>Carries out one <c>manage_window</c> call from its raw arguments and reports back.</summary>
    public static string Handle(string argumentsJson)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return "The window arguments could not be read.";
        }

        var action = Str(root, "action").Trim().ToLowerInvariant();
        var match = Str(root, "match").Trim();

        return action switch
        {
            "" or "list" => ListText(),
            "focus" or "show" or "front" => Focus(match),
            "close" or "quit" => Close(match),
            _ => $"Unknown action \"{action}\". Use list, focus or close.",
        };
    }

    // ---- actions ----

    private static string ListText()
    {
        var windows = Windows();

        if (windows.Count == 0)
        {
            return "No visible application windows are open.";
        }

        var sb = new StringBuilder("Open windows:");

        foreach (var w in windows)
        {
            sb.Append("\n- [pid ").Append(w.Pid).Append(", ").Append(w.Process).Append("] ").Append(w.Title);
        }

        return sb.ToString();
    }

    private static string Focus(string match)
    {
        if (Find(match) is not { } window)
        {
            return match.Length == 0
                ? "Say which window to focus, by a word from its title or its pid."
                : $"No open window matched \"{match}\".";
        }

        if (IsIconic(window.Hwnd))
        {
            ShowWindow(window.Hwnd, SwRestore);
        }

        BringWindowToTop(window.Hwnd);
        var ok = SetForegroundWindow(window.Hwnd);

        return ok
            ? $"Brought \"{window.Title}\" (pid {window.Pid}) to the front."
            : $"Restored \"{window.Title}\" (pid {window.Pid}); Windows would not let it take focus while another app is active.";
    }

    private static string Close(string match)
    {
        var targets = match.Length == 0 ? [] : Windows().Where(w => Matches(w, match)).ToList();

        if (targets.Count == 0)
        {
            return match.Length == 0
                ? "Say which window to close, by a word from its title or its pid."
                : $"No open window matched \"{match}\".";
        }

        foreach (var w in targets)
        {
            // A polite close — the app can save or prompt — rather than killing the process.
            PostMessage(w.Hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        return targets.Count == 1
            ? $"Asked \"{targets[0].Title}\" (pid {targets[0].Pid}) to close."
            : $"Asked {targets.Count} windows to close.";
    }

    // ---- enumeration ----

    private readonly record struct Win(IntPtr Hwnd, string Title, int Pid, string Process);

    private static Win? Find(string match)
    {
        if (match.Length == 0)
        {
            return null;
        }

        foreach (var w in Windows())
        {
            if (Matches(w, match))
            {
                return w;
            }
        }

        return null;
    }

    private static bool Matches(Win w, string match) =>
        (int.TryParse(match, out var pid) && w.Pid == pid)
        || w.Title.Contains(match, StringComparison.OrdinalIgnoreCase)
        || w.Process.Contains(match, StringComparison.OrdinalIgnoreCase);

    private static List<Win> Windows()
    {
        var list = new List<Win>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var length = GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var buffer = new StringBuilder(length + 1);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            var title = buffer.ToString();

            if (title.Length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);

            // Never RedBloom's own windows — the agent must not be able to close or hide the app it
            // is talking through.
            if ((int)pid == Environment.ProcessId)
            {
                return true;
            }

            list.Add(new Win(hwnd, title, (int)pid, ProcessName((int)pid)));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // ---- Win32 ----

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
