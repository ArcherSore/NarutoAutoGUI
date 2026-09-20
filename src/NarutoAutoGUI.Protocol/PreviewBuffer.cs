using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NarutoAutoGUI.Protocol;

// Callers serialize access to an instance. Dispose must follow the last read/write.
[SupportedOSPlatform("windows")]
public sealed class PreviewBuffer : IDisposable
{
    public const int MaximumPixelBytes =
        ProtocolConstants.MaximumPreviewPixelWidth * ProtocolConstants.MaximumPreviewPixelHeight * 4;
    public const int HeaderBytes = 4096;
    public const int Capacity = HeaderBytes + MaximumPixelBytes;
    private const int Magic = 0x50525632;
    private readonly FileStream _file;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeWaitHandle _mutex;
    private readonly bool _writer;
    private readonly byte[] _header = new byte[HeaderBytes];
    private readonly string _path;
    private bool _disposed;

    private PreviewBuffer(PreviewDescriptor descriptor, bool writer)
    {
        ValidateDescriptor(descriptor);
        Descriptor = descriptor;
        _writer = writer;
        var directory = PrepareDirectory(writer);
        if (writer) {
            CleanDeadOwners(directory);
        }
        _path = Path.Combine(directory, FileName(descriptor));
        RejectReparsePoints(_path);
        _file = new FileStream(_path, writer ? FileMode.CreateNew : FileMode.Open,
            writer ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try {
            if (writer) {
                _file.SetLength(Capacity);
            } else if (_file.Length != Capacity) {
                throw new InvalidDataException("Preview 映射容量非法。");
            }
            var access = writer ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            _map = MemoryMappedFile.CreateFromFile(_file, null, Capacity, access, HandleInheritability.None, true);
            try {
                _view = _map.CreateViewAccessor(0, Capacity, access);
                try {
                    _mutex = OpenMutex(descriptor, writer);
                    if (writer) {
                        WriteIdentity();
                        _view.WriteArray(0, _header, 0, HeaderBytes);
                    }
                } catch {
                    _view.Dispose();
                    throw;
                }
            } catch {
                _map.Dispose();
                throw;
            }
        } catch {
            _file.Dispose();
            if (writer) {
                File.Delete(_path);
            }
            throw;
        }
    }

    public PreviewDescriptor Descriptor { get; }

    public static PreviewBuffer Create(PreviewIdentity identity)
    {
        using var process = Process.GetCurrentProcess();
        var descriptor = new PreviewDescriptor(identity, process.Id, process.StartTime.ToUniversalTime().Ticks, 1);
        return new PreviewBuffer(descriptor, writer: true);
    }

    public static PreviewBuffer OpenRead(PreviewDescriptor descriptor) => new(descriptor, writer: false);

    public bool TryPublish(long generation, long revision, DateTime sampledAtUtc, int width, int height, byte[] pixels)
    {
        var length = PixelLength(width, height);
        if (pixels.Length < length || generation <= 0 || revision <= 0 || sampledAtUtc.Kind != DateTimeKind.Utc) {
            throw new ArgumentOutOfRangeException(nameof(pixels));
        }
        return TryWrite(new PreviewFrameInfo(PreviewState.Streaming, generation, revision, sampledAtUtc, width, height),
            pixels, length);
    }

    public bool TryClear(long generation, PreviewState state) =>
        TryWrite(new PreviewFrameInfo(state, generation, 0, DateTime.UnixEpoch, 0, 0), null, 0);

    private bool TryWrite(PreviewFrameInfo frame, byte[]? pixels, int length)
    {
        if (!_writer) {
            throw new InvalidOperationException("Preview reader 不能写入。");
        }
        if (!TryLock()) {
            return false;
        }
        try {
            _view.Write(8, 0);
            PutInt(12, (int)frame.State);
            PutLong(64, frame.Generation);
            PutLong(72, frame.Revision);
            PutLong(80, frame.SampledAtUtc.Ticks);
            PutInt(88, frame.Width);
            PutInt(92, frame.Height);
            PutInt(96, frame.Width * 4);
            PutInt(100, length);
            if (pixels is not null) {
                _view.WriteArray(HeaderBytes, pixels, 0, length);
            }
            _view.WriteArray(12, _header, 12, HeaderBytes - 12);
            _view.Write(8, 1);
            return true;
        } finally {
            ReleaseMutex(_mutex);
        }
    }

    public bool TryRead(byte[] pixels, out PreviewFrameInfo? frame) => TryReadCore(pixels, out frame);

    public bool TryReadInfo(out PreviewFrameInfo? frame) => TryReadCore(null, out frame);

    private bool TryReadCore(byte[]? pixels, out PreviewFrameInfo? frame)
    {
        frame = null;
        if (pixels is not null && pixels.Length < MaximumPixelBytes) {
            throw new ArgumentOutOfRangeException(nameof(pixels));
        }
        if (!TryLock()) {
            return false;
        }
        try {
            _view.ReadArray(0, _header, 0, HeaderBytes);
            if (GetInt(0) != Magic || GetInt(4) != 1
                || new Guid(_header.AsSpan(16, 16)) != Descriptor.Identity.WorkerInstanceId
                || new Guid(_header.AsSpan(32, 16)) != Descriptor.Identity.SubscriptionId
                || GetInt(48) != checked((int)Descriptor.Identity.ChildSessionId)
                || GetInt(52) != Descriptor.OwnerPid || GetLong(56) != Descriptor.OwnerStartedAtUtc) {
                throw new InvalidDataException("Preview 映射身份或版本非法。");
            }
            if (GetInt(8) == 0) {
                return false;
            }
            if (GetInt(8) != 1) {
                throw new InvalidDataException("Preview 提交标记非法。");
            }
            var state = (PreviewState)GetInt(12);
            var generation = GetLong(64);
            var revision = GetLong(72);
            var width = GetInt(88);
            var height = GetInt(92);
            if (!Enum.IsDefined(state) || generation < 0 || revision < 0) {
                throw new InvalidDataException("Preview 帧状态非法。");
            }
            if (state == PreviewState.Streaming) {
                var length = PixelLength(width, height);
                if (generation == 0 || revision == 0 || GetInt(96) != width * 4 || GetInt(100) != length) {
                    throw new InvalidDataException("Preview 像素长度非法。");
                }
                if (pixels is not null) {
                    _view.ReadArray(HeaderBytes, pixels, 0, length);
                }
            } else if (revision != 0 || width != 0 || height != 0 || GetInt(96) != 0 || GetInt(100) != 0) {
                throw new InvalidDataException("Preview 空帧非法。");
            }
            frame = new PreviewFrameInfo(state, generation, revision,
                new DateTime(GetLong(80), DateTimeKind.Utc), width, height);
            return true;
        } finally {
            ReleaseMutex(_mutex);
        }
    }

    public static void ValidateDescriptor(PreviewDescriptor descriptor)
    {
        if (descriptor.FormatVersion != 1 || descriptor.OwnerPid <= 0 || descriptor.OwnerStartedAtUtc <= 0
            || descriptor.Identity.WorkerInstanceId == Guid.Empty || descriptor.Identity.SubscriptionId == Guid.Empty
            || descriptor.Identity.ChildSessionId > int.MaxValue) {
            throw new InvalidDataException("Preview descriptor 非法。");
        }
    }

    private static int PixelLength(int width, int height)
    {
        if (width is <= 0 or > ProtocolConstants.MaximumPreviewPixelWidth
            || height is <= 0 or > ProtocolConstants.MaximumPreviewPixelHeight) {
            throw new ArgumentOutOfRangeException(nameof(width), "Preview 尺寸超过 640×360。");
        }
        return checked(width * height * 4);
    }

    private void WriteIdentity()
    {
        PutInt(0, Magic);
        PutInt(4, 1);
        Descriptor.Identity.WorkerInstanceId.TryWriteBytes(_header.AsSpan(16, 16));
        Descriptor.Identity.SubscriptionId.TryWriteBytes(_header.AsSpan(32, 16));
        PutInt(48, checked((int)Descriptor.Identity.ChildSessionId));
        PutInt(52, Descriptor.OwnerPid);
        PutLong(56, Descriptor.OwnerStartedAtUtc);
    }

    private void PutInt(int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(offset), value);
    private void PutLong(int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(offset), value);
    private int GetInt(int offset) => BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(offset));
    private long GetLong(int offset) => BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(offset));

    private bool TryLock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = WaitForSingleObject(_mutex, 0);
        if (result == 0x80) {
            ReleaseMutex(_mutex);
            throw new IOException("Preview Mutex abandoned，丢弃可能不完整的画面。");
        }
        return result switch {
            0 => true,
            0x102 => false,
            _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };
    }

    private static string FileName(PreviewDescriptor descriptor) =>
        $"{descriptor.OwnerPid}-{descriptor.OwnerStartedAtUtc}-{descriptor.Identity.WorkerInstanceId:N}"
        + $"-{descriptor.Identity.SubscriptionId:N}.frame";

    private static string PrepareDirectory(bool create)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NarutoAutoGUI", "preview");
        RejectReparsePoints(root);
        if (!create) {
            if (!Directory.Exists(root)) {
                throw new DirectoryNotFoundException("Preview 映射目录不存在。");
            }
            return root;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }) {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
        }
        var directory = new DirectoryInfo(root);
        directory.Create(security);
        directory.SetAccessControl(security);
        return root;
    }

    private static void CleanDeadOwners(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.frame")) {
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var pid) || pid <= 0
                || !long.TryParse(parts[1], out var started) || started <= 0
                || !Guid.TryParseExact(parts[2], "N", out _) || !Guid.TryParseExact(parts[3], "N", out _)) {
                continue;
            }
            try {
                RejectReparsePoints(file);
                try {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == started) {
                        continue;
                    }
                } catch (ArgumentException) {
                    // PID no longer exists; the file cannot belong to a live owner.
                }
                File.Delete(file);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) {
                // Keep resources whose ownership or access cannot be verified.
            }
        }
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) {
                throw new IOException("Preview 路径不允许重解析点。");
            }
        }
    }

    private static SafeWaitHandle OpenMutex(PreviewDescriptor descriptor, bool writer)
    {
        var name = $@"Global\NarutoAutoGUI.Preview.{descriptor.Identity.WorkerInstanceId:N}"
            + $".{descriptor.Identity.SubscriptionId:N}";
        if (!writer) {
            var opened = OpenMutexW(0x00100001, false, name);
            if (opened.IsInvalid) {
                opened.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return opened;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var sddl = $"D:P(A;;GA;;;{identity.User!.Value})(A;;GA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var security, out _)) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try {
            var attributes = new SecurityAttributes {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security
            };
            var handle = CreateMutexW(ref attributes, false, name);
            var error = Marshal.GetLastWin32Error();
            if (handle.IsInvalid || error == 183) {
                handle.Dispose();
                throw new Win32Exception(error, "Preview Mutex 创建失败或名称已存在。");
            }
            return handle;
        } finally {
            LocalFree(security);
        }
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _view.Dispose();
        _map.Dispose();
        _file.Dispose();
        _mutex.Dispose();
        if (_writer) {
            File.Delete(_path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public nint Descriptor; public int InheritHandle; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes attributes, bool owned, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle OpenMutexW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
