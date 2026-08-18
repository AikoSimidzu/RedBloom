using System.Runtime.InteropServices;
using System.Text.Json;

namespace RedBloom.Services;

/// <summary>
/// Lets an agent move and click the mouse, and type on the keyboard, so it can drive an app it can
/// see in a screenshot rather than only launch and watch it. Mouse coordinates are screen pixels —
/// the same ones a whole-screen screenshot shows, since the app runs per-monitor DPI-aware, so a
/// point read off that picture is the point clicked here.
/// </summary>
public static class InputTools
{
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;

    private const ushort VkReturn = 0x0D;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLWin = 0x5B;

    /// <summary>The action a mouse call names, so a view can confirm the first click before running.</summary>
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

    /// <summary>Carries out one <c>control_mouse</c> call from its raw arguments and reports back.</summary>
    public static string Handle(string argumentsJson)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return "The mouse arguments could not be read.";
        }

        var action = Str(root, "action").Trim().ToLowerInvariant();
        var button = Str(root, "button").Trim().ToLowerInvariant();

        // "position" only reads the cursor, so it is allowed even while paused; anything that moves
        // or clicks is a real input action and is refused when the user has hit the panic key.
        if (action is not ("" or "position" or "where") && InputGuard.Paused)
        {
            return "Input is paused by the user (panic key). It will not act until they resume.";
        }

        using var _ = action is "" or "position" or "where" ? null : InputGuard.Begin();

        return action switch
        {
            "" or "position" or "where" => Position(),
            "move" => Move(root),
            "click" => Click(root, button.Length == 0 ? "left" : button, taps: 1),
            "double" or "doubleclick" or "double_click" => Click(root, "left", taps: 2),
            "right" or "rightclick" or "right_click" => Click(root, "right", taps: 1),
            "middle" => Click(root, "middle", taps: 1),
            "drag" => Drag(root, button.Length == 0 ? "left" : button),
            "scroll" or "wheel" => Scroll(root),
            _ => $"Unknown action \"{action}\". Use move, click, double, right, drag or scroll.",
        };
    }

    /// <summary>Carries out one <c>type_keys</c> call — typing text, or pressing a key or a chord.</summary>
    public static string HandleKey(string argumentsJson)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return "The keyboard arguments could not be read.";
        }

        if (InputGuard.Paused)
        {
            return "Input is paused by the user (panic key). It will not act until they resume.";
        }

        using var _ = InputGuard.Begin();

        var text = Str(root, "text");

        if (text.Length > 0)
        {
            TypeText(text);
            return $"Typed {text.Length} character(s).";
        }

        var keys = Str(root, "keys").Trim();

        if (keys.Length > 0)
        {
            return PressChord(keys);
        }

        return "Give either \"text\" to type, or \"keys\" to press (e.g. \"enter\", \"ctrl+s\", \"alt+F4\").";
    }

    // ---- actions ----

    private static string Position()
    {
        GetCursorPos(out var p);
        return $"The cursor is at {p.X}, {p.Y}.";
    }

    private static string Move(JsonElement root)
    {
        if (!Point(root, out var x, out var y))
        {
            return "Give the point to move to as x and y (screen pixels).";
        }

        SetCursorPos(x, y);
        return $"Moved the cursor to {x}, {y}.";
    }

    private static string Click(JsonElement root, string button, int taps)
    {
        // A click may name a point, or fall on wherever the cursor already is.
        if (Point(root, out var x, out var y))
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(20);
        }
        else
        {
            GetCursorPos(out var p);
            (x, y) = (p.X, p.Y);
        }

        var (down, up) = ButtonCodes(button);

        if (down == 0)
        {
            return $"Unknown button \"{button}\". Use left, right or middle.";
        }

        for (var i = 0; i < taps; i++)
        {
            MouseEvent(down, 0, 0, 0, IntPtr.Zero);
            MouseEvent(up, 0, 0, 0, IntPtr.Zero);

            if (i + 1 < taps)
            {
                System.Threading.Thread.Sleep(60);
            }
        }

        var what = taps == 2 ? "Double-clicked" : "Clicked";
        return $"{what} the {button} button at {x}, {y}.";
    }

    private static string Drag(JsonElement root, string button)
    {
        if (!Point(root, out var x, out var y)
            || !Int(root, "x2", out var x2)
            || !Int(root, "y2", out var y2))
        {
            return "Give the drag as x, y (from) and x2, y2 (to), in screen pixels.";
        }

        var (down, up) = ButtonCodes(button);

        if (down == 0)
        {
            return $"Unknown button \"{button}\". Use left, right or middle.";
        }

        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(30);
        MouseEvent(down, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(30);

        // Stepped rather than jumped, so an app that follows the pointer during a drag sees the
        // whole path instead of only the endpoints.
        const int steps = 10;
        for (var i = 1; i <= steps; i++)
        {
            SetCursorPos(x + (x2 - x) * i / steps, y + (y2 - y) * i / steps);
            System.Threading.Thread.Sleep(15);
        }

        MouseEvent(up, 0, 0, 0, IntPtr.Zero);
        return $"Dragged from {x}, {y} to {x2}, {y2} with the {button} button.";
    }

    private static string Scroll(JsonElement root)
    {
        if (Point(root, out var x, out var y))
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(20);
        }

        // Positive is up, negative is down, in notches — one notch is 120 wheel units.
        var notches = Int(root, "amount", out var a) ? a : (Int(root, "y", out _) ? 0 : -3);
        MouseEvent(Wheel, 0, 0, notches * 120, IntPtr.Zero);

        return $"Scrolled {(notches >= 0 ? "up" : "down")} {Math.Abs(notches)} notch(es).";
    }

    // ---- keyboard ----

    /// <summary>Types a run of text as Unicode, so any character lands whatever the layout is.</summary>
    private static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            // A newline in the text is sent as a real Enter rather than a stray character.
            if (ch is '\n')
            {
                TapVirtualKey(VkReturn);
                continue;
            }

            if (ch is '\r')
            {
                continue;
            }

            SendUnicode(ch);
        }
    }

    /// <summary>
    /// Presses a key or a chord written like "ctrl+s" or "alt+F4": the modifiers are held down, the
    /// final key tapped, then the modifiers released.
    /// </summary>
    private static string PressChord(string chord)
    {
        var parts = chord.Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return "No key was given.";
        }

        var mods = new List<ushort>();
        ushort key = 0;

        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();

            switch (lower)
            {
                case "ctrl" or "control": mods.Add(VkControl); break;
                case "alt": mods.Add(VkMenu); break;
                case "shift": mods.Add(VkShift); break;
                case "win" or "super" or "meta": mods.Add(VkLWin); break;
                default:
                    if (KeyOf(lower) is not { } vk)
                    {
                        return $"Unknown key \"{part}\".";
                    }

                    key = vk;
                    break;
            }
        }

        if (key == 0)
        {
            return "A chord needs a key besides the modifiers, e.g. \"ctrl+s\".";
        }

        foreach (var mod in mods)
        {
            KeyDown(mod);
        }

        KeyDown(key);
        KeyUp(key);

        // Released in reverse, the usual order a real hand lets go of a chord.
        for (var i = mods.Count - 1; i >= 0; i--)
        {
            KeyUp(mods[i]);
        }

        return $"Pressed {chord}.";
    }

    /// <summary>The virtual-key code for a named key, or null when the name is not one we map.</summary>
    private static ushort? KeyOf(string name)
    {
        switch (name)
        {
            case "enter" or "return": return VkReturn;
            case "tab": return 0x09;
            case "esc" or "escape": return 0x1B;
            case "space" or "spacebar": return 0x20;
            case "backspace" or "back": return 0x08;
            case "delete" or "del": return 0x2E;
            case "insert" or "ins": return 0x2D;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup" or "pgup": return 0x21;
            case "pagedown" or "pgdn": return 0x22;
            case "up": return 0x26;
            case "down": return 0x28;
            case "left": return 0x25;
            case "right": return 0x27;
        }

        // Function keys F1..F24.
        if (name.Length >= 2 && name[0] == 'f' && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24)
        {
            return (ushort)(0x70 + fn - 1);
        }

        // A single printable character: letters and digits map straight to their VK, which for
        // ASCII letters and digits is the upper-case character code.
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);

            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
        }

        return null;
    }

    // ---- helpers ----

    private static (uint Down, uint Up) ButtonCodes(string button) => button switch
    {
        "left" => (LeftDown, LeftUp),
        "right" => (RightDown, RightUp),
        "middle" => (MiddleDown, MiddleUp),
        _ => (0u, 0u),
    };

    private static bool Point(JsonElement root, out int x, out int y)
    {
        var okX = Int(root, "x", out x);
        var okY = Int(root, "y", out y);
        return okX && okY;
    }

    private static bool Int(JsonElement root, string name, out int value)
    {
        value = 0;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v))
        {
            return false;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value))
        {
            return true;
        }

        // Some endpoints send numbers as strings; accept those rather than refusing the click.
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out value);
    }

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointL point);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, int dx, int dy, int data, IntPtr extra);

    // ---- keyboard Win32 (SendInput) ----

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private static void SendUnicode(char ch)
    {
        Send(new INPUT { Type = InputKeyboard, U = new INPUTUNION { Keyboard = new KEYBDINPUT { Scan = ch, Flags = KeyEventUnicode } } });
        Send(new INPUT { Type = InputKeyboard, U = new INPUTUNION { Keyboard = new KEYBDINPUT { Scan = ch, Flags = KeyEventUnicode | KeyEventKeyUp } } });
    }

    private static void KeyDown(ushort vk) =>
        Send(new INPUT { Type = InputKeyboard, U = new INPUTUNION { Keyboard = new KEYBDINPUT { Vk = vk } } });

    private static void KeyUp(ushort vk) =>
        Send(new INPUT { Type = InputKeyboard, U = new INPUTUNION { Keyboard = new KEYBDINPUT { Vk = vk, Flags = KeyEventKeyUp } } });

    private static void TapVirtualKey(ushort vk)
    {
        KeyDown(vk);
        KeyUp(vk);
    }

    private static void Send(INPUT input) => SendInput(1, [input], Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
