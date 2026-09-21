using System.Reflection;
using System.Windows;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyMinimizedPreview(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "preview-minimize"), (window, directory) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object? Get(string field) => typeof(MainWindow).GetField(field, flags)!.GetValue(window);
            void Set(string field, object value)
            {
                typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
            }
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config", "maanop-config.json"));
            var manifest = project.CreateLaunchManifest(Guid.NewGuid());
            var available = new DependencyCheck(true, "self-test", null);
            var worker = new WorkerSnapshot(ProtocolConstants.SnapshotVersion, DateTime.UtcNow, 1,
                manifest.WorkerInstanceId, Environment.ProcessId, 7, "self-test", ProtocolConstants.ProtocolVersion,
                manifest.RuntimeProfileDigest, manifest.Project, WorkerState.Ready, null,
                new DependencyStatus(DateTime.UtcNow, "self-test", "self-test",
                    available, available, available, available, available), RunState.Idle, null, null, 0, 0);
            Set("_sessionSnapshot", new ChildSessionSnapshot(ChildSessionState.ConnectedHidden, 7, 1, "fixture"));
            Set("_workerSnapshot", new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, worker, ""));
            // Stand in for an already running subscription; exercise the real window state handlers.
            var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            Set("_previewPollingTask", running.Task);
            Set("_previewWorkerInstanceId", worker.WorkerInstanceId);
            Set("_previewPollingCancellation", cancellation);
            window.WindowState = WindowState.Minimized;
            PumpOnboarding();
            if (Get("_previewPaused") is not true || cancellation.IsCancellationRequested
                || !ReferenceEquals(Get("_previewPollingTask"), running.Task)) {
                throw new InvalidOperationException("最小化必须暂停并保留已有预览订阅。");
            }
            window.WindowState = WindowState.Normal;
            PumpOnboarding();
            if (Get("_previewPaused") is not false || cancellation.IsCancellationRequested
                || !ReferenceEquals(Get("_previewPollingTask"), running.Task)) {
                throw new InvalidOperationException("恢复窗口不应替换预览订阅。");
            }
            window.Hide();
            PumpOnboarding();
            if (!cancellation.IsCancellationRequested || Get("_previewPollingTask") is not null) {
                throw new InvalidOperationException("隐藏到托盘仍须撤销预览订阅。");
            }
        });
    }
}
