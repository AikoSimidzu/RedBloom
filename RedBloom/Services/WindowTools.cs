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
    private const byte VkMenu = 0x12;
    private const uint KeyEventKeyUp = 0x0002;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>A window call's outcome: the text the model reads, and a screenshot when it asked for one.</summary>
    public readonly record struct Outcome(string Text, byte[]? Png = null);

    /// <summary>The action a call names, so a view can decide whether to confirm it before running.</summary>
    public static string ActionOf(string argumentsJson)
    {
        try
        {
            var root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
            return Str(root, "action").Trim().ToLowerInvariant();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Carries out one <c>manage_window</c> call from its raw arguments and reports back.</summary>
    public static Outcome Handle(string argumentsJson)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return new Outcome("The window arguments could not be read.");
        }

        var action = Str(root, "action").Trim().ToLowerInvariant();
        var match = Str(root, "match").Trim();

        // Reading — list and screenshot — is always allowed; launching, focusing or closing an app
        // changes the machine's state, so it is refused while the user has input paused.
        var changesState = action is "launch" or "open" or "start" or "run"
            or "focus" or "show" or "front" or "close" or "quit";

        if (changesState && InputGuard.Paused)
        {
            return new Outcome("Input is paused by the user (panic key). It will not act until they resume.");
        }

        using var _ = changesState ? InputGuard.Begin() : null;

        return action switch
        {
            "" or "list" => new Outcome(ListText()),
            "launch" or "open" or "start" or "run" => new Outcome(Launch(match)),
            "screenshot" or "capture" or "shot" or "see" or "view" => Screenshot(match),
            "focus" or "show" or "front" => new Outcome(Focus(match)),
            "close" or "quit" => new Outcome(Close(match)),
            _ => new Outcome($"Unknown action \"{action}\". Use launch, list, screenshot, focus or close."),
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

    private static string Launch(string what)
    {
        what = what.Trim().Trim('"');

        if (what.Length == 0)
        {
            return "Say what to launch — an app name, an executable path, or a document.";
        }

        try
        {
            // UseShellExecute starts it detached and through the shell, so it keeps running and the
            // call returns at once — unlike run_command, which would wait until the app is closed.
            var process = Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });
            return process is not null
                ? $"Launched \"{what}\" (pid {process.Id}). It is running; use list/focus/close to manage it."
                : $"Could not launch \"{what}\".";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            return $"Could not launch \"{what}\": {ex.Message}";
        }
    }

    private static Outcome Screenshot(string match)
    {
        // A named window is brought to the front first so the pixels captured are its own and not
        // whatever is sitting on top; with no match the whole virtual screen is taken.
        System.Drawing.Rectangle rect;
        string what;

        if (match.Length > 0)
        {
            if (Find(match) is not { } window)
            {
                return new Outcome($"No open window matched \"{match}\", so there was nothing to capture.");
            }

            if (IsIconic(window.Hwnd))
            {
                ShowWindow(window.Hwnd, SwRestore);
            }

            KeybdEvent(VkMenu, 0, 0, IntPtr.Zero);
            KeybdEvent(VkMenu, 0, KeyEventKeyUp, IntPtr.Zero);
            SetForegroundWindow(window.Hwnd);
            System.Threading.Thread.Sleep(200);

            rect = GetWindowRect(window.Hwnd, out var r) ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Screen();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                rect = Screen();
            }

            what = $"\"{window.Title}\" (pid {window.Pid})";
        }
        else
        {
            rect = Screen();
            what = "the whole screen";
        }

        try
        {
            using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            }

            using var stream = new System.IO.MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return new Outcome($"Screenshot of {what}.", stream.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OutOfMemoryException)
        {
            return new Outcome($"Could not capture {what}: {ex.Message}");
        }
    }

    private static System.Drawing.Rectangle Screen() => new(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
        Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)));

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

        // Windows only lets the foreground process change the foreground window; a synthetic Alt
        // tap satisfies that rule so focus works even when RedBloom is not the active window.
        KeybdEvent(VkMenu, 0, 0, IntPtr.Zero);
        KeybdEvent(VkMenu, 0, KeyEventKeyUp, IntPtr.Zero);

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

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte key, byte scan, uint flags, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
