using System.Runtime.InteropServices;
using System.Text;
using Veil.Diagnostics;

namespace Veil.Services.Terminal;

internal sealed class TerminalSession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint WAIT_INFINITE = 0xFFFFFFFF;
    private const uint STILL_ACTIVE = 259;
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;
    private const uint StartupGracePeriodMs = 350;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetErrorMode(uint uMode);

    private IntPtr _hPseudoConsole = IntPtr.Zero;
    private IntPtr _hPipeInWrite = IntPtr.Zero;
    private IntPtr _hPipeOutRead = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;
    private IntPtr _attributeList = IntPtr.Zero;

    private FileStream? _outStream;
    private FileStream? _inStream;

    private readonly CancellationTokenSource _cts = new();
    private int _exitSignaled;
    private bool _disposed;
    private volatile bool _hasExited;

    // Output buffer: holds chunks emitted before a handler is registered so initial shell
    // output (prompt, banner) is not silently discarded.
    private readonly List<byte[]> _outputBuffer = [];
    private readonly object _outputGate = new();
    private Action<byte[]>? _outputHandler;

    public string ProfileName { get; }
    public uint? ExitCode { get; private set; }
    public bool IsAlive => !_disposed && _hProcess != IntPtr.Zero && !_hasExited && IsProcessAlive();

    public event Action<byte[]>? OutputReceived
    {
        add
        {
            if (value is null) return;
            lock (_outputGate)
            {
                _outputHandler += value;
                // Replay any output that arrived before this handler was attached.
                if (_outputBuffer.Count > 0)
                {
                    foreach (var chunk in _outputBuffer)
                    {
                        value(chunk);
                    }
                    _outputBuffer.Clear();
                }
            }
        }
        remove
        {
            lock (_outputGate)
            {
                _outputHandler -= value;
            }
        }
    }

    public event Action? SessionExited;

    internal TerminalSession(TerminalProfile profile, int cols, int rows)
    {
        ProfileName = profile.DisplayName;

        try
        {
            StartSession(profile, cols, rows);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"TerminalSession failed to start for profile {profile.DisplayName}.", ex);
            Dispose();
            throw;
        }
    }

    private void StartSession(TerminalProfile profile, int cols, int rows)
    {
        if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || !File.Exists(profile.ExecutablePath))
        {
            throw new FileNotFoundException($"Terminal executable was not found for profile {profile.DisplayName}.", profile.ExecutablePath);
        }

        IntPtr hPipeInRead = IntPtr.Zero;
        IntPtr hPipeOutWrite = IntPtr.Zero;

        try
        {
            if (!CreatePipe(out hPipeInRead, out _hPipeInWrite, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException($"CreatePipe (in) failed: {Marshal.GetLastWin32Error()}");
            }

            if (!CreatePipe(out _hPipeOutRead, out hPipeOutWrite, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException($"CreatePipe (out) failed: {Marshal.GetLastWin32Error()}");
            }

            var consoleSize = new COORD
            {
                X = (short)Math.Clamp(cols, 1, 9999),
                Y = (short)Math.Clamp(rows, 1, 9999)
            };

            int hr = CreatePseudoConsole(consoleSize, hPipeInRead, hPipeOutWrite, 0, out _hPseudoConsole);
            if (hr != 0)
            {
                throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
            }

            LaunchProcess(profile, ref hPipeInRead, ref hPipeOutWrite);

            _outStream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(_hPipeOutRead, false), FileAccess.Read, bufferSize: 4096, isAsync: false);
            _inStream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(_hPipeInWrite, false), FileAccess.Write, bufferSize: 4096, isAsync: false);
            Task.Run(() => ReadLoop(_cts.Token));
            EnsureProcessStarted(profile);
        }
        finally
        {
            if (hPipeInRead != IntPtr.Zero)
            {
                CloseHandle(hPipeInRead);
            }

            if (hPipeOutWrite != IntPtr.Zero)
            {
                CloseHandle(hPipeOutWrite);
            }
        }
    }

    private void LaunchProcess(TerminalProfile profile, ref IntPtr pseudoConsoleInput, ref IntPtr pseudoConsoleOutput)
    {
        IntPtr attrListBuffer = IntPtr.Zero;

        try
        {
            IntPtr attrListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);

            attrListBuffer = Marshal.AllocHGlobal(attrListSize);
            _attributeList = attrListBuffer;

            if (!InitializeProcThreadAttributeList(attrListBuffer, 1, 0, ref attrListSize))
            {
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
            }

            if (!UpdateProcThreadAttribute(
                    attrListBuffer,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
            }

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = IntPtr.Zero,
                    hStdError = IntPtr.Zero
                },
                lpAttributeList = attrListBuffer
            };

            string commandLine = BuildCommandLine(profile);
            var cmdLineBuilder = new StringBuilder(commandLine);

            uint errorMode = SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX;
            uint previousErrorMode = SetErrorMode(errorMode);
            bool success;
            PROCESS_INFORMATION pi;

            try
            {
                success = CreateProcessW(
                    lpApplicationName: null,
                    lpCommandLine: cmdLineBuilder,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    lpEnvironment: IntPtr.Zero,
                    lpCurrentDirectory: null,
                    lpStartupInfo: ref si,
                    lpProcessInformation: out pi);
            }
            finally
            {
                SetErrorMode(previousErrorMode);
            }

            if (!success)
            {
                throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
            }

            _hProcess = pi.hProcess;
            _hThread = pi.hThread;

            CloseAndInvalidateHandle(ref pseudoConsoleInput);
            CloseAndInvalidateHandle(ref pseudoConsoleOutput);
            Task.Run(MonitorProcessLifetime);
        }
        catch
        {
            if (attrListBuffer != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrListBuffer);
                Marshal.FreeHGlobal(attrListBuffer);
                _attributeList = IntPtr.Zero;
            }
            throw;
        }
    }

    private async Task MonitorProcessLifetime()
    {
        if (_hProcess == IntPtr.Zero)
        {
            return;
        }

        await Task.Yield();
        WaitForSingleObject(_hProcess, WAIT_INFINITE);

        if (GetExitCodeProcess(_hProcess, out uint exitCode))
        {
            ExitCode = exitCode;
        }

        _hasExited = true;
        SignalSessionExited();
    }

    private static string BuildCommandLine(TerminalProfile profile)
    {
        string executable = QuoteCommandToken(profile.ExecutablePath);
        return string.IsNullOrWhiteSpace(profile.Arguments)
            ? executable
            : $"{executable} {profile.Arguments}";
    }

    private static void CloseAndInvalidateHandle(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    private void EnsureProcessStarted(TerminalProfile profile)
    {
        if (_hProcess == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to launch {profile.DisplayName}.");
        }

        uint waitResult = WaitForSingleObject(_hProcess, StartupGracePeriodMs);
        if (waitResult == WAIT_OBJECT_0)
        {
            if (GetExitCodeProcess(_hProcess, out uint exitCode))
            {
                ExitCode = exitCode;
                _hasExited = true;
                throw new InvalidOperationException($"{profile.DisplayName} failed during startup with exit code {FormatExitCode(exitCode)}.");
            }

            throw new InvalidOperationException($"{profile.DisplayName} exited during startup.");
        }

        if (waitResult != WAIT_TIMEOUT)
        {
            throw new InvalidOperationException($"WaitForSingleObject failed while starting {profile.DisplayName}: {Marshal.GetLastWin32Error()}");
        }

        if (!IsProcessAlive())
        {
            throw new InvalidOperationException($"{profile.DisplayName} exited during startup.");
        }
    }

    internal static string FormatExitCode(uint exitCode)
    {
        return exitCode switch
        {
            0xC0000142 => "0xC0000142 (STATUS_DLL_INIT_FAILED)",
            0xC0000135 => "0xC0000135 (STATUS_DLL_NOT_FOUND)",
            0xC0000005 => "0xC0000005 (STATUS_ACCESS_VIOLATION)",
            _ => $"0x{exitCode:X8}"
        };
    }

    private static string QuoteCommandToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ', StringComparison.Ordinal) || value.Contains('\t', StringComparison.Ordinal)
            ? $"\"{value}\""
            : value;
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        // Capture stream reference once; avoids a TOCTOU race where Dispose nulls
        // _outStream between the null check and the Read call.
        var stream = _outStream;

        try
        {
            while (!cancellationToken.IsCancellationRequested && stream is not null)
            {
                int bytesRead;
                try
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                var data = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                DispatchOutput(data);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("TerminalSession read loop error.", ex);
        }
        finally
        {
            _hasExited = true;
            // Only signal session exited if the exit was not triggered by an intentional Dispose.
            if (!_disposed)
            {
                SignalSessionExited();
            }
        }
    }

    private void DispatchOutput(byte[] data)
    {
        lock (_outputGate)
        {
            if (_outputHandler is not null)
            {
                _outputHandler(data);
            }
            else
            {
                _outputBuffer.Add(data);
            }
        }
    }

    internal void Write(byte[] data)
    {
        if (_disposed || _inStream is null || data.Length == 0)
        {
            return;
        }

        try
        {
            _inStream.Write(data, 0, data.Length);
            _inStream.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    internal void Resize(int cols, int rows)
    {
        if (_disposed || _hPseudoConsole == IntPtr.Zero)
        {
            return;
        }

        var size = new COORD
        {
            X = (short)Math.Clamp(cols, 1, 9999),
            Y = (short)Math.Clamp(rows, 1, 9999)
        };

        ResizePseudoConsole(_hPseudoConsole, size);
    }

    private bool IsProcessAlive()
    {
        if (_hProcess == IntPtr.Zero)
        {
            return false;
        }

        return GetExitCodeProcess(_hProcess, out uint exitCode) && exitCode == STILL_ACTIVE;
    }

    private void SignalSessionExited()
    {
        if (_disposed || Interlocked.Exchange(ref _exitSignaled, 1) != 0)
        {
            return;
        }

        SessionExited?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        // Kill child processes via the job object first. This closes the write end of
        // the output pipe (from the child's side), which makes the blocking ReadFile in
        // ReadLoop return immediately with EOF instead of hanging until handles are closed.
        // Close pseudoconsole — sends close signal to attached processes.
        if (_hPseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_hPseudoConsole);
            _hPseudoConsole = IntPtr.Zero;
        }

        // Close the input write handle so the child cannot receive further input.
        if (_hPipeInWrite != IntPtr.Zero)
        {
            CloseHandle(_hPipeInWrite);
            _hPipeInWrite = IntPtr.Zero;
        }

        // Dispose streams. _outStream wraps _hPipeOutRead with ownsHandle=false so
        // Dispose marks the stream closed without closing the underlying handle.
        try { _outStream?.Dispose(); } catch { }
        try { _inStream?.Dispose(); } catch { }
        _outStream = null;
        _inStream = null;

        // Close the output read handle. At this point the child is dead and ReadLoop
        // has already exited (child exit closed the write end), so this is safe.
        if (_hPipeOutRead != IntPtr.Zero)
        {
            CloseHandle(_hPipeOutRead);
            _hPipeOutRead = IntPtr.Zero;
        }

        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }

        if (_hThread != IntPtr.Zero)
        {
            CloseHandle(_hThread);
            _hThread = IntPtr.Zero;
        }

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }

        lock (_outputGate)
        {
            _outputBuffer.Clear();
        }

        _cts.Dispose();
    }
}
