using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

// Owns only this pretask's ordinary process descendants. Existing processes are never assigned to this job.
internal sealed class PretaskProcess : IDisposable
{
    private const uint KillOnClose = 0x2000;
    private readonly Action<string, string, string> _log;
    private readonly CancellationTokenSource _outputStop = new();
    private SafeFileHandle? _job;
    private SafeFileHandle? _process;
    private NamedPipeServerStream? _stdout;
    private NamedPipeServerStream? _stderr;
    private Task _output = Task.CompletedTask;
    private bool _assigned;
    private bool _preserveOutput;
    private bool _disposed;

    internal PretaskProcess(Action<string, string, string> log)
    {
        _log = log;
    }

    internal async Task StartAsync(PretaskCommand command, string directory, CancellationToken cancellationToken)
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        Check(!_job.IsInvalid, "CreateJobObject");
        SetKillOnClose(true);
        var stdout = await CreateOutputPipeAsync(cancellationToken);
        _stdout = stdout.Server;
        using var stdoutWriter = stdout.Client;
        var stderr = await CreateOutputPipeAsync(cancellationToken);
        _stderr = stderr.Server;
        using var stderrWriter = stderr.Client;
        using var input = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var handles = new[] {
            input.DangerousGetHandle(), stdoutWriter.SafePipeHandle.DangerousGetHandle(),
            stderrWriter.SafePipeHandle.DangerousGetHandle()
        };
        foreach (var handle in handles) {
            Check(SetHandleInformation(handle, 1, 1), "SetHandleInformation");
        }

        using var attributes = new HandleList(handles);
        var startup = new StartupInfoEx {
            Info = new StartupInfo {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                StandardInput = handles[0], StandardOutput = handles[1], StandardError = handles[2]
            },
            Attributes = attributes.Pointer
        };
        var executable = ResolveExecutable(command.Executable, directory);
        var text = string.Join(" ", new[] { executable }.Concat(command.Arguments).Select(QuoteArgument));
        if (text.Length >= 32767) {
            throw new InvalidDataException("Pretask 命令行超过 Windows 长度上限。 ");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Check(CreateProcess(executable, new StringBuilder(text), IntPtr.Zero, IntPtr.Zero, true,
            0x00080000 | 0x00000004 | 0x08000000, IntPtr.Zero, directory, ref startup, out var created),
            "CreateProcess");
        _process = new SafeFileHandle(created.Process, true);
        using var thread = new SafeFileHandle(created.Thread, true);
        Check(AssignProcessToJobObject(_job, _process), "AssignProcessToJobObject");
        _assigned = true;
        _output = Task.WhenAll(PumpAsync(_stdout, "stdout"), PumpAsync(_stderr, "stderr"));
        cancellationToken.ThrowIfCancellationRequested();
        Check(ResumeThread(thread) != uint.MaxValue, "ResumeThread");
    }

    internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (_process is { IsInvalid: false } process) {
            var wait = WaitForSingleObject(process, 0);
            if (wait == 0) {
                Check(GetExitCodeProcess(process, out var exitCode), "GetExitCodeProcess");
                return unchecked((int)exitCode);
            }
            Check(wait == 258, "WaitForSingleObject");
            await Task.Delay(40, cancellationToken);
        }
        throw new InvalidOperationException("Pretask 尚未创建进程。 ");
    }

    internal void PreserveChildren()
    {
        SetKillOnClose(false);
        _preserveOutput = true;
    }

    // False keeps the job, process and output handles: an unconfirmed tree must stay owned for recovery.
    internal async Task<bool> ReleaseAsync(TimeSpan timeout)
    {
        if (!await TerminateAndConfirmAsync(timeout)) {
            return false;
        }
        await CloseOutputAsync();
        Dispose();
        return true;
    }

    private async Task<bool> TerminateAndConfirmAsync(TimeSpan timeout)
    {
        if (_process is null) {
            return true;
        }
        if (_assigned) {
            Check(TerminateJobObject(_job!, 1), "TerminateJobObject");
        } else if (WaitForSingleObject(_process, 0) != 0) {
            Check(TerminateProcess(_process, 1), "TerminateProcess");
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < timeout) {
            if (_assigned) {
                Check(QueryInformationJobObject(_job!, 1, out var accounting,
                    Marshal.SizeOf<JobAccounting>(), IntPtr.Zero), "QueryInformationJobObject");
                if (accounting.ActiveProcesses == 0) {
                    return true;
                }
            } else if (WaitForSingleObject(_process, 0) == 0) {
                return true;
            }
            await Task.Delay(40);
        }
        return false;
    }

    private async Task CloseOutputAsync()
    {
        // Used only after failure/cancellation has confirmed termination of the owned process tree.
        await Task.WhenAny(_output, Task.Delay(100));
        _outputStop.Cancel();
        _stdout?.Dispose();
        _stderr?.Dispose();
        try {
            await _output.WaitAsync(TimeSpan.FromSeconds(2));
        } catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException) {
            _log("WARN", "pretask.output", "Pretask 输出收尾未完成，已关闭本次读取端。 ");
        }
    }

    private async Task PumpAsync(Stream pipe, string name)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, false), true, 2048, leaveOpen: true);
        var buffer = new char[2048];
        var retained = 0;
        var truncated = false;
        try {
            while (true) {
                var read = await reader.ReadAsync(buffer.AsMemory(), _outputStop.Token);
                if (read == 0) {
                    return;
                }
                var keep = Math.Min(read, Math.Max(0, 16384 - retained));
                if (keep > 0) {
                    _log("INFO", "pretask.output", $"{name}: {new string(buffer, 0, keep)}");
                    retained += keep;
                }
                if (keep < read && !truncated) {
                    truncated = true;
                    _log("WARN", "pretask.output", $"{name}: 输出已截断，继续读取并丢弃后续内容。 ");
                }
            }
        } catch (Exception exception) when (exception is OperationCanceledException or IOException
            or ObjectDisposedException) {
            // Failure/cancellation closes the reader after process-tree cleanup.
        }
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> CreateOutputPipeAsync(
        CancellationToken cancellationToken)
    {
        var name = $"NarutoAutoGUI.Pretask.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        try {
            var connected = server.WaitForConnectionAsync(cancellationToken);
            await client.ConnectAsync(cancellationToken);
            await connected;
            return (server, client);
        } catch {
            client.Dispose();
            server.Dispose();
            throw;
        }
    }

    private void SetKillOnClose(bool enabled)
    {
        var limits = new JobLimits { Basic = new BasicLimits { Flags = enabled ? KillOnClose : 0 } };
        Check(SetInformationJobObject(_job!, 9, ref limits, Marshal.SizeOf<JobLimits>()), "SetInformationJobObject");
    }

    internal static string ResolveExecutable(string executable, string directory)
    {
        if (Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['\\', '/']) >= 0) {
            return Path.GetFullPath(executable, directory);
        }
        var paths = new[] { directory, Environment.SystemDirectory }.Concat(
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path))) {
            var candidate = Path.Combine(path.Trim('"'), executable);
            if (File.Exists(candidate)) {
                return Path.GetFullPath(candidate);
            }
            if (!Path.HasExtension(executable) && File.Exists(candidate + ".exe")) {
                return Path.GetFullPath(candidate + ".exe");
            }
        }
        throw new FileNotFoundException("Pretask 可执行程序不存在。", executable);
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value) {
            if (character == '\\') {
                slashes++;
                continue;
            }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _process?.Dispose();
        _job?.Dispose();
        if (_preserveOutput) {
            // Success releases process ownership, but descendants can still own the pipe writers. Keep draining
            // with the existing per-stream log budget until EOF, without delaying the Run or its state lock.
            _ = Task.Run(DrainPreservedOutputAsync);
        } else {
            DisposeOutput();
        }
    }

    private async Task DrainPreservedOutputAsync()
    {
        try {
            await _output;
        } catch (Exception exception) {
            _log("WARN", "pretask.output", $"保留子进程的输出读取失败：{exception.GetBaseException().Message}");
        } finally {
            DisposeOutput();
        }
    }

    private void DisposeOutput()
    {
        _stdout?.Dispose();
        _stderr?.Dispose();
        _outputStop.Dispose();
    }

    private static void Check(bool success, string operation)
    {
        if (!success) {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Pretask {operation} 失败。 ");
        }
    }

    private sealed class HandleList : IDisposable
    {
        private readonly IntPtr _values;
        internal IntPtr Pointer { get; }

        internal HandleList(IntPtr[] handles)
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            _values = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
            var initialized = false;
            try {
                Check(InitializeProcThreadAttributeList(Pointer, 1, 0, ref size), "InitializeProcThreadAttributeList");
                initialized = true;
                Marshal.Copy(handles, 0, _values, handles.Length);
                Check(UpdateProcThreadAttribute(Pointer, 0, (IntPtr)0x20002, _values,
                    (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");
            } catch {
                if (initialized) {
                    DeleteProcThreadAttributeList(Pointer);
                }
                Marshal.FreeHGlobal(_values);
                Marshal.FreeHGlobal(Pointer);
                throw;
            }
        }

        public void Dispose()
        {
            DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(_values);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedBytes;
        internal IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo Info;
        internal IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int kind, out JobAccounting value, int size, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, nuint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
}
