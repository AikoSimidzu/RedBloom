namespace RedBloom.Services;

/// <summary>
/// The brake and the warning light for an agent driving the machine. While an agent is moving the
/// mouse, typing or closing windows, this shows a floating indicator and a panic hotkey that stops
/// everything at once — so the user is never surprised by input they did not make, and can always
/// take back control instantly.
/// </summary>
public static class InputGuard
{
    private static int _active;

    /// <summary>True once the panic key has been hit; every input action refuses until it is cleared.</summary>
    public static bool Paused { get; private set; }

    /// <summary>Raised when the number of in-flight input actions crosses zero, so a light can turn on and off.</summary>
    public static event Action<bool>? ActivityChanged;

    /// <summary>Raised by the panic hotkey, so open chats and rooms can cancel their running turns.</summary>
    public static event Action? PanicRequested;

    /// <summary>True while at least one agent input action is in flight.</summary>
    public static bool Active => Volatile.Read(ref _active) > 0;

    /// <summary>
    /// Marks the start of an input action — a click, a keystroke, a window close. Returns a token
    /// that must be disposed when the action finishes, which turns the indicator off once the last
    /// one is done.
    /// </summary>
    public static IDisposable Begin()
    {
        if (Interlocked.Increment(ref _active) == 1)
        {
            ActivityChanged?.Invoke(true);
        }

        return new Scope();
    }

    /// <summary>Fired by the global hotkey: pause all input and cancel running turns.</summary>
    public static void Panic()
    {
        Paused = true;
        PanicRequested?.Invoke();
    }

    /// <summary>Lifts the pause, so agents may act again — the user chose to resume.</summary>
    public static void Resume() => Paused = false;

    private sealed class Scope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;

            if (Interlocked.Decrement(ref _active) == 0)
            {
                ActivityChanged?.Invoke(false);
            }
        }
    }
}
