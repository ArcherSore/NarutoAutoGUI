using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    // A rejected connection.open ends only that connection: the pipe server keeps serving and nothing else changes.
    private static async Task VerifyConnectionOpenRejectionAsync(
        AppLogger logger, string testDirectory, string workerPath, uint session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stateDirectory = Path.Combine(testDirectory, "connection-open");
        var recordPath = Path.Combine(stateDirectory, "worker.json");
        // No PID yet, so validation reaches the client-process lookup instead of stopping at a PID mismatch.
        var record = new WorkerAdmissionRecord(
            Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(), session, null,
            "self-test-runtime", DateTime.UtcNow);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllBytes(recordPath,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));
        var original = File.ReadAllBytes(recordPath);
        var pipeName = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";
        await using var coordinator = new WorkerCoordinator(
            logger, stateDirectory, workerPath, pipeName, usePipeAcl: false);
        await coordinator.WaitForServerReadyAsync(timeout.Token);
        var observation = coordinator.Snapshot;

        async Task RequireNoEffectAsync(string name)
        {
            await Task.Delay(100, timeout.Token);
            Require(File.ReadAllBytes(recordPath).SequenceEqual(original) && coordinator.Snapshot == observation,
                $"{name}：被拒绝的连接修改了 Admission Record 或观察状态。");
        }

        JsonObject OpenData() => new() {
            ["workerInstanceId"] = record.WorkerInstanceId,
            ["launchToken"] = record.LaunchToken,
            ["runtimeProfileDigest"] = record.RuntimeProfileDigest
        };
        var unknownField = OpenData();
        unknownField["unexpected"] = 1;
        var wrongType = OpenData();
        wrongType["workerInstanceId"] = 123;
        foreach (var (name, data) in new[] { ("unknown-field", unknownField), ("wrong-type", wrongType) }) {
            await using (var pipe = await ConnectRawAsync(pipeName, timeout.Token)) {
                var requestId = Guid.NewGuid();
                await pipe.WriteAsync(
                    WireEnvelope.Request(ProtocolOperations.ConnectionOpen, requestId, data), timeout.Token);
                var response = await pipe.ReadAsync(timeout.Token);
                Require(response is { Success: false, Error.Code: "invalid_request" }
                    && response.RequestId == requestId, $"{name}：畸形 connection.open 未被结构化拒绝。");
            }
            await RequireNoEffectAsync(name);
        }

        await SendOpenFromExitedClientAsync(pipeName, record, timeout.Token);
        await RequireNoEffectAsync("exited-client");

        await using var worker = await OpenConnectionAsync(pipeName, record, 0, timeout.Token);
        await WaitForFreshAsync(coordinator, timeout.Token);
    }

    private static async Task<ProtocolConnection> ConnectRawAsync(string pipeName, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try {
            await pipe.ConnectAsync(5000, cancellationToken);
            return new ProtocolConnection(pipe);
        } catch {
            await pipe.DisposeAsync();
            throw;
        }
    }

    // The pipe's client PID is a helper that connected and then exited before connection.open was sent.
    private static async Task SendOpenFromExitedClientAsync(
        string pipeName, WorkerAdmissionRecord record, CancellationToken cancellationToken)
    {
        var script = "$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', '" + pipeName
            + "', [System.IO.Pipes.PipeDirection]::InOut); $pipe.Connect(5000); "
            + "[Console]::Out.WriteLine($pipe.SafePipeHandle.DangerousGetHandle().ToInt64()); "
            + "[Console]::Out.Flush(); [void][Console]::In.ReadLine()";
        using var helper = Process.Start(new ProcessStartInfo("powershell.exe") {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("无法启动 Pipe 客户端辅助进程。");
        IntPtr duplicate;
        try {
            var line = await helper.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new InvalidOperationException("Pipe 客户端辅助进程未连接。");
            using var self = Process.GetCurrentProcess();
            if (!DuplicateHandle(helper.Handle, new IntPtr(long.Parse(line)), self.Handle, out duplicate,
                0, false, DuplicateSameAccess)) {
                throw new InvalidOperationException($"复制 Pipe 客户端句柄失败：{Marshal.GetLastWin32Error()}。");
            }
        } finally {
            helper.StandardInput.Close();
            await helper.WaitForExitAsync(cancellationToken);
        }

        await using var pipe = new ProtocolConnection(new NamedPipeClientStream(
            PipeDirection.InOut, isAsync: false, isConnected: true, new SafePipeHandle(duplicate, ownsHandle: true)));
        await pipe.WriteAsync(WireEnvelope.Request(ProtocolOperations.ConnectionOpen, Guid.NewGuid(),
            new ConnectionOpenRequest(record.WorkerInstanceId, record.LaunchToken, record.RuntimeProfileDigest)),
            cancellationToken);
        try {
            Require(await pipe.ReadAsync(cancellationToken) is null or { Success: false },
                "已退出客户端的 connection.open 被接纳。");
        } catch (IOException) {
            // The server closing the rejected connection is the expected outcome.
        }
    }

    private const uint DuplicateSameAccess = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
}
