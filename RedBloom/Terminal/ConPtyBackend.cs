using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static RedBloom.Terminal.ConPtyNative;

namespace RedBloom.Terminal;

/// <summary>Runs a local shell inside a Windows pseudoconsole.</summary>
public sealed class ConPtyBackend : ITerminalBackend
{
    private readonly ShellProfile _profile;
    private readonly Lock _resizeLock = new();

    private IntPtr _pseudoConsole = IntPtr.Zero;
    private IntPtr _attributeList = IntPtr.Zero;
    private SafeProcessHandle? _processHandle;
    private SafeProcessHandle? _threadHandle;
    private FileStream? _writeToChild;
    private FileStream? _readFromChild;
    /// <summary>
    /// A pseudoconsole always speaks UTF-8: the console host keeps text as UTF-16 internally
    /// and encodes its VT stream as UTF-8, whatever codepage the child itself is using.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private int _closedRaised;
    private bool _disposed;

    public ConPtyBackend(ShellProfile profile) => _profile = profile;

    public event Action<string>? Output;
    public event Action<string>? Closed;

    public bool IsRunning => _pseudoConsole != IntPtr.Zero && !_disposed;

    public Task StartAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!CreatePipe(out var childInputRead, out var childInputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the pseudoconsole input pipe.");
        }

        if (!CreatePipe(out var childOutputRead, out var childOutputWrite, IntPtr.Zero, 0))
        {
            childInputRead.Dispose();
            childInputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the pseudoconsole output pipe.");
        }

        var size = new COORD { X = (short)Math.Max(1, columns), Y = (short)Math.Max(1, rows) };
        var hr = CreatePseudoConsole(size, childInputRead, childOutputWrite, 0, out _pseudoConsole);

        // The pseudoconsole duplicated the ends it needs; ours are dead weight and, more
        // importantly, would keep the pipes alive past child exit and stall the read loop.
        childInputRead.Dispose();
        childOutputWrite.Dispose();

        if (hr != 0)
        {
            childInputWrite.Dispose();
            childOutputRead.Dispose();
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            StartChildProcess();
        }
        catch
        {
            childInputWrite.Dispose();
            childOutputRead.Dispose();
            Dispose();
            throw;
        }

        _writeToChild = new FileStream(childInputWrite, FileAccess.Write);
        _readFromChild = new FileStream(childOutputRead, FileAccess.Read);

        new Thread(PumpOutput) { IsBackground = true, Name = $"conpty-read:{_profile.Name}" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = $"conpty-wait:{_profile.Name}" }.Start();

        return Task.CompletedTask;
    }

    public void Write(string data)
    {
        if (_disposed || _writeToChild is null || string.IsNullOrEmpty(data))
        {
            return;
        }

        try
        {
            var bytes = Utf8NoBom.GetBytes(data);
            _writeToChild.Write(bytes, 0, bytes.Length);
            _writeToChild.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child went away between our check and the write; the wait thread reports it.
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_resizeLock)
        {
            if (_disposed || _pseudoConsole == IntPtr.Zero)
            {
                return;
            }

            var size = new COORD { X = (short)Math.Max(1, columns), Y = (short)Math.Max(1, rows) };
            ResizePseudoConsole(_pseudoConsole, size);
        }
    }

    private void StartChildProcess()
    {
        var attributeCount = 1;
        var listSize = IntPtr.Zero;

        // First call always fails with ERROR_INSUFFICIENT_BUFFER and reports the size we need.
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref listSize);
        _attributeList = Marshal.AllocHGlobal(listSize);

        if (!InitializeProcThreadAttributeList(_attributeList, attributeCount, 0, ref listSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize the process attribute list.");
        }

        if (!UpdateProcThreadAttribute(
                _attributeList,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _pseudoConsole,
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to attach the pseudoconsole to the child process.");
        }

        var startupInfo = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
            lpAttributeList = _attributeList,
        };

        var workingDirectory = _profile.StartingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // CreateProcessW may write into the command-line buffer, so it has to be memory we own.
        var commandLine = Marshal.StringToHGlobalUni(_profile.BuildCommandLine());
        try
        {
            var created = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo);

            if (!created)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Failed to start '{_profile.Executable}'.");
            }

            _processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            _threadHandle = new SafeProcessHandle(processInfo.hThread, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }
    }

    private void PumpOutput()
    {
        var stream = _readFromChild;
        if (stream is null)
        {
            return;
        }

        var buffer = new byte[8192];
        // A decoder keeps partial multi-byte sequences across reads, so a UTF-8 character
        // split across a pipe boundary still arrives intact.
        var decoder = Utf8NoBom.GetDecoder();
        var chars = new char[Utf8NoBom.GetMaxCharCount(buffer.Length)];

        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var decoded = decoder.GetChars(buffer, 0, read, chars, 0);
                if (decoded > 0)
                {
                    Output?.Invoke(new string(chars, 0, decoded));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Expected when the session is torn down while a read is in flight.
        }
    }

    private void WaitForExit()
    {
        var handle = _processHandle;
        if (handle is null || handle.IsInvalid)
        {
            return;
        }

        // Hold a reference for the duration of the wait so a concurrent Dispose cannot
        // close the handle out from under WaitForSingleObject.
        var referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            if (!referenced)
            {
                return;
            }

            var raw = handle.DangerousGetHandle();
            WaitForSingleObject(raw, INFINITE);
            GetExitCodeProcess(raw, out var exitCode);
            RaiseClosed(exitCode == 0
                ? $"{_profile.Name} exited."
                : $"{_profile.Name} exited with code {exitCode}.");
        }
        finally
        {
            if (referenced)
            {
                handle.DangerousRelease();
            }
        }
    }

    private void RaiseClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(reason);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the pseudoconsole terminates the child and closes its pipe ends, which
        // unblocks the read loop with EOF.
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        _writeToChild?.Dispose();
        _readFromChild?.Dispose();
        _writeToChild = null;
        _readFromChild = null;

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }

        // SafeHandle defers the actual close until the wait thread releases its reference.
        _threadHandle?.Dispose();
        _processHandle?.Dispose();

        RaiseClosed($"{_profile.Name} closed.");
    }
}
