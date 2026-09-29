using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NarutoAutoGUI.Worker;

internal enum AdmissionLiveness
{
    Alive,
    Stale,
    Indeterminate
}

// One process as a snapshot saw it. A null field could not be read and must never be treated as a mismatch.
internal sealed record WorkerProcessEntry(int ProcessId, uint? SessionId, string? ImageName);

internal static class WorkerAdmissionInspection
{
    // Decides only from system state; the coordinator answers Alive itself for connected or launching records.
    internal static AdmissionLiveness Classify(
        WorkerAdmissionRecord record, string workerImageName, DateTime nowUtc,
        Func<IReadOnlyList<WorkerProcessEntry>> captureProcesses, Func<IReadOnlyCollection<uint>> enumerateSessions)
    {
        IReadOnlyList<WorkerProcessEntry> processes;
        try {
            processes = captureProcesses();
        } catch (Exception) {
            return AdmissionLiveness.Indeterminate;
        }
        if (record.WorkerPid is int pid) {
            return ClassifyProcess(processes.Where(process => process.ProcessId == pid).ToArray(),
                record.ChildSessionId, workerImageName);
        }

        // RunEx may already have been submitted, so neither age nor an empty session proves a pending launch dead.
        if (record.CreatedAtUtc > nowUtc) {
            return AdmissionLiveness.Indeterminate;
        }
        IReadOnlyCollection<uint> sessions;
        try {
            sessions = enumerateSessions();
        } catch (Exception) {
            return AdmissionLiveness.Indeterminate;
        }
        return sessions.Contains(record.ChildSessionId)
            || processes.Any(process => process.SessionId is null || process.SessionId == record.ChildSessionId)
                ? AdmissionLiveness.Indeterminate
                : AdmissionLiveness.Stale;
    }

    // Returns processes that are a Worker in the session, and processes that cannot be ruled out.
    internal static (int[] Running, int[] Unreadable) FindWorkers(
        IReadOnlyList<WorkerProcessEntry> processes, uint sessionId, string workerImageName)
    {
        var candidates = processes.Where(process => process.SessionId is null || process.SessionId == sessionId)
            .Where(process => process.ImageName is null || IsSameImage(process.ImageName, workerImageName))
            .ToArray();
        return (candidates.Where(process => process.SessionId is not null && process.ImageName is not null)
                .Select(process => process.ProcessId).ToArray(),
            candidates.Where(process => process.SessionId is null || process.ImageName is null)
                .Select(process => process.ProcessId).ToArray());
    }

    // Process.ProcessName drops ".exe" while other enumerations keep it; compare names without it.
    internal static bool IsSameImage(string left, string right) =>
        string.Equals(NormalizeImageName(left), NormalizeImageName(right), StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<WorkerProcessEntry> CaptureProcesses()
    {
        var processes = Process.GetProcesses();
        try {
            var entries = new WorkerProcessEntry[processes.Length];
            for (var index = 0; index < processes.Length; index++) {
                var process = processes[index];
                uint? sessionId = null;
                string? imageName = null;
                try {
                    sessionId = checked((uint)process.SessionId);
                } catch (Exception exception) when (IsInspectionFailure(exception)) {
                }
                try {
                    imageName = process.ProcessName;
                } catch (Exception exception) when (IsInspectionFailure(exception)) {
                }
                entries[index] = new WorkerProcessEntry(process.Id, sessionId, imageName);
            }
            return entries;
        } finally {
            foreach (var process in processes) {
                process.Dispose();
            }
        }
    }

    internal static IReadOnlyCollection<uint> EnumerateSessionIds()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举 Windows Session。");
        }
        try {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            var sessions = new HashSet<uint>();
            for (var index = 0; index < count; index++) {
                sessions.Add(Marshal.PtrToStructure<WtsSessionInfo>(buffer + index * size).SessionId);
            }
            return sessions;
        } finally {
            WTSFreeMemory(buffer);
        }
    }

    private static AdmissionLiveness ClassifyProcess(
        IReadOnlyList<WorkerProcessEntry> matches, uint childSessionId, string workerImageName)
    {
        if (matches.Count == 0) {
            return AdmissionLiveness.Stale;
        }
        if (matches.Count > 1) {
            return AdmissionLiveness.Indeterminate;
        }
        // A process never changes its Session or image, so either mismatch proves the PID now belongs to another.
        var process = matches[0];
        if (process.SessionId is uint sessionId && sessionId != childSessionId
            || process.ImageName is string imageName && !IsSameImage(imageName, workerImageName)) {
            return AdmissionLiveness.Stale;
        }
        return process.SessionId is null || process.ImageName is null
            ? AdmissionLiveness.Indeterminate
            : AdmissionLiveness.Alive;
    }

    private static string NormalizeImageName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsInspectionFailure(Exception exception) =>
        exception is InvalidOperationException or Win32Exception or NotSupportedException;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
