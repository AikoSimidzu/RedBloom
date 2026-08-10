using System.Runtime.InteropServices;
using Microsoft.Win32;
using RedBloom.Services;

namespace RedBloom.Services.Ai;

/// <summary>How well a model of a given size would run on this machine.</summary>
public enum Fit
{
    /// <summary>Fits in graphics memory with room to work. Fast.</summary>
    Good,

    /// <summary>Fits, but only just. It will run; expect it to be slower and to fill the card.</summary>
    Tight,

    /// <summary>Too big for the card, small enough for system memory. Runs on the processor, slowly.</summary>
    Heavy,

    /// <summary>Larger than this machine's memory. It will not load.</summary>
    No,
}

/// <summary>
/// What this machine has to run a model with, and what that means for a given file.
/// </summary>
/// <remarks>
/// The judgement is deliberately coarse — four words, not a predicted tokens-per-second. What
/// actually decides the speed is whether the weights sit in graphics memory or in system memory,
/// and that is a comparison of two numbers anyone can check. A confident-looking figure would be
/// wrong often enough to be worse than no figure at all.
/// </remarks>
public static class MachineFit
{
    /// <summary>
    /// Weights are not the whole cost: the context, the key-value cache and the runner itself all
    /// want room beside them. A fifth over the file size is the usual rule of thumb.
    /// </summary>
    private const double Overhead = 1.2;

    /// <summary>
    /// Below this, a card's reported memory is not really its own.
    /// </summary>
    /// <remarks>
    /// Integrated graphics report an aperture rather than a private pool — commonly the 2 GB
    /// sentinel 0x7FFFF000 — while actually drawing on system memory. Treating that as a hard
    /// limit would mark every worthwhile model unrunnable on a machine that runs them fine.
    /// </remarks>
    private const long SmallestRealCard = 3L * 1024 * 1024 * 1024;

    private static readonly Lazy<long> InstalledMemory = new(ReadSystemMemory);
    private static readonly Lazy<long> GraphicsMemory = new(ReadGraphicsMemory);

    /// <summary>Bytes of system memory installed.</summary>
    public static long SystemBytes => InstalledMemory.Value;

    /// <summary>Bytes on the largest graphics adapter, or zero when none could be read.</summary>
    public static long GraphicsBytes => GraphicsMemory.Value;

    /// <summary>True when the graphics chip has memory of its own worth counting.</summary>
    public static bool HasDedicatedCard => GraphicsBytes >= SmallestRealCard;

    /// <summary>The name of the graphics adapter, for the line describing the machine.</summary>
    public static string GraphicsName { get; private set; } = string.Empty;

    /// <summary>One line describing the machine, for the top of the model list.</summary>
    public static string Summary
    {
        get
        {
            // Reading the adapter is what learns its name, so that has to happen first — the
            // other way round the line says "graphics" on the very first call and the real name
            // on every one after it.
            var dedicated = HasDedicatedCard;
            var card = GraphicsName.Length > 0 ? GraphicsName : Say("L_MachineGraphics");

            return dedicated
                ? Say("L_MachineCard", Gigabytes(SystemBytes), Gigabytes(GraphicsBytes), card)
                : Say("L_MachineShared", Gigabytes(SystemBytes), card);
        }
    }

    public static double Gigabytes(long bytes) => Math.Round(bytes / 1024d / 1024d / 1024d, 1);

    /// <summary>How a model file of this size would fare here.</summary>
    public static Fit Rate(long bytes)
    {
        if (bytes <= 0 || SystemBytes <= 0)
        {
            return Fit.No;
        }

        var needed = (long)(bytes * Overhead);

        if (HasDedicatedCard)
        {
            if (needed <= GraphicsBytes)
            {
                // Comfortably inside the card, with the working room already counted.
                return needed <= GraphicsBytes * 0.85 ? Fit.Good : Fit.Tight;
            }

            // Too big for the card: it falls back to the processor, which works but is slow.
            return needed <= SystemBytes * 0.75 ? Fit.Heavy : Fit.No;
        }

        // Shared memory: there is no second pool to run out of, so the only question is how much
        // of the machine the model would take. Two thirds is already most of a working computer.
        return needed switch
        {
            _ when needed <= SystemBytes * 0.35 => Fit.Good,
            _ when needed <= SystemBytes * 0.55 => Fit.Tight,
            _ when needed <= SystemBytes * 0.75 => Fit.Heavy,
            _ => Fit.No,
        };
    }

    public static string Describe(Fit fit) => Say(fit switch
    {
        Fit.Good => "L_FitGood",
        Fit.Tight => "L_FitTight",
        Fit.Heavy => "L_FitHeavy",
        _ => "L_FitNo",
    });

    /// <summary>Why it was rated that way, in one sentence.</summary>
    public static string Explain(Fit fit, long bytes)
    {
        // On shared memory every answer is about the machine's one pool; on a card, about the
        // card. The two readings need different sentences, not one with a word swapped.
        var shared = !HasDedicatedCard || fit == Fit.No;

        var key = (shared ? "L_FitShared" : "L_FitCard") + fit switch
        {
            Fit.Good => "Good",
            Fit.Tight => "Tight",
            Fit.Heavy => "Heavy",
            _ => "No",
        };

        return Say(key, Gigabytes(bytes), shared ? Gigabytes(SystemBytes) : Gigabytes(GraphicsBytes));
    }

    /// <summary>
    /// One localised line, filled in.
    /// </summary>
    /// <remarks>
    /// Formatted with the current culture rather than the invariant one: these are sentences
    /// shown to a person, and a decimal point where they expect a comma reads as a typo.
    /// </remarks>
    private static string Say(string key, params object[] parts) =>
        parts.Length == 0
            ? LocalizationService.T(key)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture, LocalizationService.T(key), parts);

    private static long ReadSystemMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };

        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : 0;
    }

    /// <summary>
    /// The largest adapter's memory, read from where the display driver records it.
    /// </summary>
    /// <remarks>
    /// Taken from the registry rather than from WMI's <c>Win32_VideoController</c>, whose
    /// AdapterRAM is a 32-bit field and so reports 4 GB for every card above that — which is
    /// every card this feature is about.
    /// </remarks>
    private static long ReadGraphicsMemory()
    {
        const string Adapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(Adapters);

            if (adapters is null)
            {
                return 0;
            }

            var largest = 0L;

            foreach (var name in adapters.GetSubKeyNames())
            {
                using var adapter = adapters.OpenSubKey(name);

                if (adapter?.GetValue("DriverDesc") is null)
                {
                    continue;
                }

                // Two spellings, and three shapes between them: a 64-bit value on modern
                // drivers, a 32-bit one or a raw byte run on older and integrated ones.
                var size = Math.Max(
                    Size(adapter.GetValue("HardwareInformation.qwMemorySize")),
                    Size(adapter.GetValue("HardwareInformation.MemorySize")));

                if (size > largest)
                {
                    largest = size;
                    GraphicsName = adapter.GetValue("DriverDesc") as string ?? string.Empty;
                }
            }

            return largest;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>A registry value that may be a QWORD, a DWORD or raw bytes.</summary>
    private static long Size(object? value) => value switch
    {
        long quad => quad,
        int word => word,
        byte[] bytes and { Length: 8 } => BitConverter.ToInt64(bytes),
        byte[] bytes and { Length: 4 } => BitConverter.ToUInt32(bytes),
        _ => 0,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
