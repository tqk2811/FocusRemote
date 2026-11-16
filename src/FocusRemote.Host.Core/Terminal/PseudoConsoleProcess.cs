using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FocusRemote.Host.Core.Terminal.Interfaces;
using Microsoft.Win32.SafeHandles;
using static FocusRemote.Host.Core.Terminal.Native.ConPtyNativeMethods;

namespace FocusRemote.Host.Core.Terminal;

/// <summary>
/// A shell running in a Windows ConPTY, inside a kill-on-close job object (its child processes die with it).
/// Threads: reader (output pipe → listener), writer (queued input → input pipe), waiter (process exit → close
/// the pseudo console while the reader keeps draining, avoiding the ClosePseudoConsole hang → report exit).
/// Input is bounded to <see cref="MaxPendingInputBytes"/>: a shell that stops reading makes further input drop
/// (<see cref="Write"/> returns false) instead of growing memory. The waiter is the single owner of the job and
/// process handles; it closes them under <c>_handleLock</c>, which Dispose takes before terminating the job.
/// </summary>
internal sealed class PseudoConsoleProcess : ITerminalProcess
{
    public const long MaxPendingInputBytes = 1024 * 1024;
    private const int ReadChunkSize = 16 * 1024;
    private static readonly TimeSpan _joinTimeout = TimeSpan.FromSeconds(5);

    private readonly ITerminalProcessListener _listener;
    private readonly BlockingCollection<ReadOnlyMemory<byte>> _input = new();
    private readonly object _consoleLock = new();
    private readonly object _handleLock = new();
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly IntPtr _job;
    private readonly IntPtr _process;
    private readonly Thread _reader;
    private readonly Thread _writer;
    private readonly Thread _waiter;
    private IntPtr _console;
    private int _finished; // 0 running, 1 exited or disposed (first one wins)
    private long _pendingInputBytes;
    private bool _handlesClosed; // under _handleLock
    private volatile bool _disposed;
    private volatile bool _readerInCallback;

    public PseudoConsoleProcess(string commandLine, string workingDirectory, int cols, int rows, ITerminalProcessListener listener)
    {
        _listener = listener;
        SafeFileHandle? consoleInput = null, inputWrite = null, outputRead = null, consoleOutput = null;
        IntPtr attributes = IntPtr.Zero;
        IntPtr console = IntPtr.Zero, job = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            if (!CreatePipe(out consoleInput, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out consoleOutput, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
            int hr = CreatePseudoConsole(ToCoord(cols, rows), consoleInput, consoleOutput, 0, out console);
            if (hr != 0)
                throw new Win32Exception(hr, "CreatePseudoConsole failed");

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(attributes);
                attributes = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            }
            // The attribute value is the HPCON itself (not a pointer to it).
            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var startup = new STARTUPINFOEX { lpAttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // Null std handles: the child must not inherit our (possibly redirected) handles; it uses the pseudo console.
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");

            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, workingDirectory, ref startup, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
            if (!AssignProcessToJobObject(job, pi.hProcess))
            {
                int error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                throw new Win32Exception(error, "AssignProcessToJobObject failed");
            }
            if (ResumeThread(pi.hThread) == uint.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                throw new Win32Exception(error, "ResumeThread failed");
            }
        }
        catch
        {
            if (pi.hThread != IntPtr.Zero)
                CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero)
                CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero)
                CloseHandle(job);
            // Pipe ends first: with nobody draining the output, ClosePseudoConsole could block (older Windows).
            inputWrite?.Dispose();
            outputRead?.Dispose();
            if (console != IntPtr.Zero)
                ClosePseudoConsole(console);
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            // The pseudo console holds its own copies of these ends.
            consoleInput?.Dispose();
            consoleOutput?.Dispose();
        }

        CloseHandle(pi.hThread);
        _process = pi.hProcess;
        _job = job;
        _console = console;
        _inputStream = new FileStream(inputWrite!, FileAccess.Write, 1, false);
        _outputStream = new FileStream(outputRead!, FileAccess.Read, 1, false);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ConPTY writer" };
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "ConPTY waiter" };
        _reader.Start();
        _writer.Start();
        _waiter.Start();
    }

    public bool Write(ReadOnlyMemory<byte> data)
    {
        // Input for a shell that already exited (or was killed) is meaningless, not dropped for lack of room.
        if (data.IsEmpty || Volatile.Read(ref _finished) != 0)
            return true;
        if (Interlocked.Add(ref _pendingInputBytes, data.Length) > MaxPendingInputBytes)
        {
            Interlocked.Add(ref _pendingInputBytes, -data.Length);
            return false;
        }
        try
        {
            if (_input.TryAdd(data.ToArray()))
                return true;
        }
        catch (InvalidOperationException)
        {
            // Completed concurrently: the shell is ending, so the input is discarded (not "queue full").
            Interlocked.Add(ref _pendingInputBytes, -data.Length);
            return true;
        }
        Interlocked.Add(ref _pendingInputBytes, -data.Length);
        return false;
    }

    public void Resize(int cols, int rows)
    {
        lock (_consoleLock)
        {
            if (_console == IntPtr.Zero)
                return;
            int hr = ResizePseudoConsole(_console, ToCoord(cols, rows));
            if (hr != 0)
                throw new Win32Exception(hr, "ResizePseudoConsole failed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _finished, 1, 0) == 0)
        {
            _disposed = true;
            lock (_handleLock)
            {
                // Closed means the process already exited: nothing left to kill.
                if (!_handlesClosed && !TerminateJobObject(_job, 1))
                    TerminateProcess(_process, 1);
            }
        }
        if (Thread.CurrentThread != _waiter)
            _waiter.Join(_joinTimeout);
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[ReadChunkSize];
        try
        {
            while (true)
            {
                int read = _outputStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                if (_disposed)
                    continue;
                _readerInCallback = true;
                try
                {
                    _listener.OnOutput(buffer.AsSpan(0, read));
                }
                finally
                {
                    _readerInCallback = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Pipe broken: the pseudo console closed.
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (ReadOnlyMemory<byte> data in _input.GetConsumingEnumerable())
            {
                Interlocked.Add(ref _pendingInputBytes, -data.Length);
                _inputStream.Write(data.Span);
                _inputStream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Pipe broken: the pseudo console closed.
        }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, INFINITE);
        GetExitCodeProcess(_process, out uint exitCode);
        bool exitedByItself = Interlocked.CompareExchange(ref _finished, 1, 0) == 0;

        // Close the pseudo console while the reader still drains the output pipe; it then sees EOF.
        IntPtr console;
        lock (_consoleLock)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        if (console != IntPtr.Zero)
            ClosePseudoConsole(console);
        // The timeout guards a reader stuck on a pipe that never reaches EOF. A reader busy in the listener (output
        // backpressure) is still delivering: keep waiting, so the exit is reported after the last output. The
        // listener returns early once the terminal is removed (closed, viewer gone), so this cannot wait forever.
        while (!_reader.Join(_joinTimeout) && _readerInCallback)
        {
        }
        _input.CompleteAdding();
        _writer.Join(_joinTimeout);
        _inputStream.Dispose();
        _outputStream.Dispose();
        lock (_handleLock)
        {
            _handlesClosed = true;
            CloseHandle(_job);
            CloseHandle(_process);
        }
        if (exitedByItself)
            _listener.OnExited(unchecked((int)exitCode));
    }

    private static COORD ToCoord(int cols, int rows)
        => new() { X = (short)Math.Clamp(cols, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };
}
