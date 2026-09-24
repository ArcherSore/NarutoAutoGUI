using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;
using WpfButton = System.Windows.Controls.Button;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyOnboarding(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "layout-stability"), (window, directory) => {
            StartAutomaticOnboarding(window);
            AssertOnboardingLayoutSettles(window);
            window.Width = 920;
            window.Height = 640;
            AssertOnboardingLayoutSettles(window);
            AssertOnboardingPlacement(window);
        });
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "auto"), (window, directory) => {
            PumpOnboarding(450);
            if (!ConfigurationDescendants((DependencyObject)window.FindName("PlanItemsPanel"))
                .OfType<System.Windows.Controls.TextBox>().Any()) {
                throw new InvalidOperationException("自动 Tour 尚未显示时，首次默认任务就应该已经展开参数。");
            }
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed) {
                throw new InvalidOperationException("启动恢复尚未结束时不得自动展示 Tour。");
            }
            CompleteOnboardingStartup(window);
            InvokeOnboarding(window, "ReevaluateOnboarding");
            PumpOnboarding(600);
            AssertOnboardingStep(window, 1, "配置自动化任务");
            ClickOnboarding(window, "OnboardingSkip");
            if (File.ReadAllText(Path.Combine(directory, "config", "onboarding.txt")) != "1"
                || File.Exists(Path.Combine(directory, "config", "onboarding-new-user.pending"))
                || !SettingsActionFor(window, "onboarding.replay").IsEnabled) {
                throw new InvalidOperationException("自动跳过须记录完成、清理资格，并允许从设置页 replay。");
            }
        });
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "replay"), (window, directory) => {
            var path = Path.Combine(directory, "config", "maanop-config.json");
            var before = File.ReadAllBytes(path);
            var pendingPath = Path.Combine(directory, "config", "onboarding-new-user.pending");
            var pending = File.ReadAllBytes(pendingPath);
            var versionPath = Path.Combine(directory, "config", "onboarding.txt");
            File.WriteAllText(versionPath, "9");
            ClickOnboarding(window, "SettingsNavigationItem");
            SettingsActionFor(window, "onboarding.replay").Execute(null);
            PumpOnboarding();
            AssertOnboardingStep(window, 1, "配置自动化任务");
            ClickOnboarding(window, "OnboardingNext");
            AssertOnboardingStep(window, 2, "查看说明并设置参数");
            ClickOnboarding(window, "ExpandPreviewButton");
            ClickOnboarding(window, "UpdateNavigationItem");
            if (((UIElement)window.FindName("PreviewOverlay")).Visibility != Visibility.Collapsed
                || ((UIElement)window.FindName("UpdateOverlay")).Visibility != Visibility.Collapsed) {
                throw new InvalidOperationException("Tour 期间底层 Preview/Update 入口不能叠加模态层。");
            }
            window.Width = 920;
            window.Height = 640;
            PumpOnboarding();
            AssertOnboardingPlacement(window);
            var root = (FrameworkElement)window.FindName("WindowOverlayRoot");
            var workspace = (FrameworkElement)window.FindName("TaskWorkspacePanel");
            var viewport = workspace.TransformToAncestor(root).TransformBounds(new Rect(workspace.RenderSize));
            var outline = ((System.Windows.Shapes.Path)window.FindName("OnboardingOutline")).Data.Bounds;
            if (outline.Top < viewport.Top || outline.Bottom > viewport.Bottom) {
                throw new InvalidOperationException("超高任务卡的高亮外扩不能越过滚动视口。");
            }
            window.Hide();
            PumpOnboarding();
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed
                || File.ReadAllText(versionPath) != "9") {
                throw new InvalidOperationException("隐藏必须暂停新手指引且不能记录完成。");
            }
            window.Show();
            PumpOnboarding();
            AssertOnboardingStep(window, 2, "查看说明并设置参数");
            ClickOnboarding(window, "OnboardingNext");
            AssertOnboardingStep(window, 3, "启动自动化");
            ClickOnboarding(window, "OnboardingNext");
            AssertOnboardingStep(window, 4, "查看挂机画面");
            if (((WpfButton)window.FindName("OnboardingNext")).Content.ToString() != "开始使用") {
                throw new InvalidOperationException("最后一步必须显示开始使用。");
            }
            ClickOnboarding(window, "OnboardingPrevious");
            AssertOnboardingStep(window, 3, "启动自动化");
            ClickOnboarding(window, "OnboardingNext");
            ClickOnboarding(window, "OnboardingNext");
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed
                || ((UIElement)window.FindName("HomeView")).Visibility != Visibility.Visible
                || !before.SequenceEqual(File.ReadAllBytes(path)) || File.ReadAllText(versionPath) != "9"
                || !pending.SequenceEqual(File.ReadAllBytes(pendingPath))) {
                throw new InvalidOperationException("Replay 必须回到首页且保持配置、完成版本和资格不变。");
            }
            var project = ProjectPlanModule.Open(directory, path);
            project.RemoveTask("RealTask");
            SetOnboardingField(window, "_projectPlan", project);
            InvokeOnboarding(window, "RenderTaskPlan");
            before = File.ReadAllBytes(path);
            SettingsActionFor(window, "onboarding.replay").Execute(null);
            PumpOnboarding();
            ClickOnboarding(window, "OnboardingNext");
            AssertOnboardingStep(window, 2, "查看说明并设置参数");
            if (!((TextBlock)window.FindName("OnboardingDescription")).Text.StartsWith("添加任务后")) {
                throw new InvalidOperationException("空计划 replay 必须介绍添加后的卡片，不得插入任务。");
            }
            ClickOnboarding(window, "OnboardingSkip");
            if (!before.SequenceEqual(File.ReadAllBytes(path))) {
                throw new InvalidOperationException("空计划 replay 不得写入配置。");
            }
        });
        VerifyOnboardingEligibility(logger, testDirectory);
    }

    private static void VerifyOnboardingEligibility(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "immediate-recovery"), (window, directory) => {
            PumpOnboarding(600);
            AssertOnboardingStep(window, 1, "配置自动化任务");
        }, recoveryCompleted: true);
        foreach (var existing in new[] { "old", "corrupt", "completed", "invalid-version" }) {
            RunOnboardingScenario(logger, Path.Combine(testDirectory, existing), (window, directory) => {
                CompleteOnboardingStartup(window);
                InvokeOnboarding(window, "ReevaluateOnboarding");
                PumpOnboarding(500);
                if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed
                    || File.Exists(Path.Combine(directory, "config", "onboarding-new-user.pending"))) {
                    throw new InvalidOperationException($"{existing} 不能自动显示 Tour 或新增首次资格。");
                }
                if (existing == "corrupt"
                    && File.ReadAllText(Path.Combine(directory, "config", "maanop-config.json")) != "broken") {
                    throw new InvalidOperationException("Tour 初始化不能覆盖损坏配置。");
                }
            }, directory => {
                var config = Path.Combine(directory, "config");
                Directory.CreateDirectory(config);
                if (existing == "old") {
                    _ = ProjectPlanModule.Open(directory, Path.Combine(config, "maanop-config.json"));
                } else if (existing == "corrupt") {
                    File.WriteAllText(Path.Combine(config, "maanop-config.json"), "broken");
                } else {
                    File.WriteAllText(Path.Combine(config, "onboarding.txt"), existing == "completed" ? "9" : "bad");
                }
            });
        }
        var interrupted = Path.Combine(testDirectory, "interrupted");
        RunOnboardingScenario(logger, interrupted, (window, directory) => {
            StartAutomaticOnboarding(window);
            ClickOnboarding(window, "OnboardingNext");
            AssertOnboardingStep(window, 2, "查看说明并设置参数");
        });
        RunOnboardingScenario(logger, interrupted, (window, directory) => {
            StartAutomaticOnboarding(window);
            AssertOnboardingStep(window, 1, "配置自动化任务");
            var source = PresentationSource.FromVisual(window)!;
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape) {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            });
            PumpOnboarding();
            if (File.ReadAllText(Path.Combine(directory, "config", "onboarding.txt")) != "1") {
                throw new InvalidOperationException("Esc 必须等价于自动 Tour 的跳过。");
            }
        });
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "tour-finish"), (window, directory) => {
            StartAutomaticOnboarding(window);
            SetOnboardingField(window, "_busy", true);
            InvokeOnboarding(window, "ReevaluateOnboarding");
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed) {
                throw new InvalidOperationException("外部 busy 操作应暂停 Tour。");
            }
            SetOnboardingField(window, "_busy", false);
            InvokeOnboarding(window, "ReevaluateOnboarding");
            PumpOnboarding();
            for (var index = 0; index < 4; index++) {
                ClickOnboarding(window, "OnboardingNext");
            }
            if (File.ReadAllText(Path.Combine(directory, "config", "onboarding.txt")) != "1") {
                throw new InvalidOperationException("开始使用必须记录自动 Tour 完成版本。");
            }
        });
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "missing-target"), (window, directory) => {
            StartAutomaticOnboarding(window);
            ((UIElement)window.FindName("RuntimeControlCard")).Visibility = Visibility.Collapsed;
            ClickOnboarding(window, "OnboardingNext");
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed
                || File.Exists(Path.Combine(directory, "config", "onboarding.txt"))) {
                throw new InvalidOperationException("必需 target 缺失必须取消且不记完成。");
            }
        });
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "completion-write-failure"), (window, directory) => {
            StartAutomaticOnboarding(window);
            Directory.CreateDirectory(Path.Combine(directory, "config", "onboarding.txt"));
            ClickOnboarding(window, "OnboardingSkip");
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed
                || !SettingsActionFor(window, "onboarding.replay").Status.Contains("未能保存")
                || !File.Exists(Path.Combine(directory, "config", "onboarding-new-user.pending"))) {
                throw new InvalidOperationException("完成版本保存失败必须关闭、提示并保留首次资格。");
            }
        });
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "modal-delay"), (window, directory) => {
            var preview = (UIElement)window.FindName("PreviewOverlay");
            preview.Visibility = Visibility.Visible;
            CompleteOnboardingStartup(window);
            InvokeOnboarding(window, "ReevaluateOnboarding");
            PumpOnboarding(500);
            if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Collapsed) {
                throw new InvalidOperationException("Preview modal 打开时自动 Tour 必须延后。");
            }
            ClickOnboarding(window, "ClosePreviewButton");
            PumpOnboarding(500);
            AssertOnboardingStep(window, 1, "配置自动化任务");
            ClickOnboarding(window, "OnboardingNext");
            PumpOnboarding(3700);
            if (((UIElement)window.FindName("OnboardingPulse")).Visibility != Visibility.Collapsed) {
                throw new InvalidOperationException("说明图标 Pulse 必须有限，不能无限循环。");
            }
        });
    }

    private static void CompleteOnboardingStartup(MainWindow window)
        => ((TaskCompletionSource)window.Tag).TrySetResult();

    private static void StartAutomaticOnboarding(MainWindow window)
    {
        CompleteOnboardingStartup(window);
        InvokeOnboarding(window, "ReevaluateOnboarding");
        PumpOnboarding(600);
        AssertOnboardingStep(window, 1, "配置自动化任务");
    }

    private static void RunOnboardingScenario(AppLogger logger, string directory,
        Action<MainWindow, string> verify, Action<string>? beforeLoad = null, bool recoveryCompleted = false,
        Func<CancellationToken, Task<Updates.EngineCheckResult>>? checkForUpdate = null,
        Func<Task>? requestExit = null)
    {
        var projectDirectory = CreateProjectFixture(directory);
        beforeLoad?.Invoke(projectDirectory);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "config"));
        File.WriteAllText(Path.Combine(projectDirectory, "config", "update-check.txt"), "false");
        using var session = new ChildSessionManager(logger);
        var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "tour-state"), "unused.exe",
            $"NarutoAutoGUI.Tour.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
        var window = new MainWindow(logger, session, new ChildSessionProgramService(logger), coordinator,
            operation => operation(), requestExit ?? (() => Task.CompletedTask), projectDirectory, checkForUpdate);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var loaded = typeof(MainWindow).GetMethod("MainWindow_Loaded", flags)!;
        window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
        var application = System.Windows.Application.Current;
        var shutdown = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (recoveryCompleted) {
            recovery.SetResult();
        }
        window.Tag = recovery;
        Task? startup = null;
        try {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            PumpOnboarding();
            Func<Task> restoreSession = () => {
                if (((UIElement)window.FindName("TaskWorkspacePanel")).Visibility != Visibility.Visible
                    || SettingsPageFor(window).Sections.SelectMany(section => section.Items)
                        .Single(item => item.Definition.SettingKey == "update.checkOnStartup").Toggle!.Value) {
                    throw new InvalidOperationException("必须先完成 Project 与更新偏好初始化，再等待 Session 恢复。");
                }
                return recovery.Task;
            };
            startup = (Task)typeof(MainWindow).GetMethod("InitializeStartupAsync", flags)!
                .Invoke(window, new object[] { restoreSession })!;
            PumpOnboarding();
            verify(window, projectDirectory);
        } finally {
            window.AllowClose();
            window.Close();
            recovery.TrySetResult();
            PumpOnboarding();
            startup?.GetAwaiter().GetResult();
            application.ShutdownMode = shutdown;
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    private static void ClickOnboarding(MainWindow window, string name)
    {
        var element = (UIElement)window.FindName(name);
        element.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        PumpOnboarding();
    }

    private static void PumpOnboarding(int milliseconds = 100)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void InvokeOnboarding(MainWindow window, string name) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private static void SetOnboardingField(MainWindow window, string name, object value) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void AssertOnboardingStep(MainWindow window, int step, string title)
    {
        if (((UIElement)window.FindName("OnboardingOverlay")).Visibility != Visibility.Visible
            || ((TextBlock)window.FindName("OnboardingTitle")).Text != title
            || ((TextBlock)window.FindName("OnboardingCounter")).Text != $"{step} / 4") {
            throw new InvalidOperationException($"新手指引第 {step} 步必须显示正确标题和计数。");
        }
        AssertOnboardingPlacement(window);
        var overlay = (FrameworkElement)window.FindName("OnboardingOverlay");
        var rootForHitTest = (FrameworkElement)window.FindName("WindowOverlayRoot");
        var outline = (System.Windows.Shapes.Path)window.FindName("OnboardingOutline");
        var hole = outline.Data.Bounds;
        var center = new System.Windows.Point(hole.X + hole.Width / 2, hole.Y + hole.Height / 2);
        var hit = rootForHitTest.InputHitTest(center) as DependencyObject;
        if (hit is null || hit != overlay && !overlay.IsAncestorOf(hit)) {
            throw new InvalidOperationException("Spotlight hole 不能成为底层控件的命中穿透区域。");
        }
        var screenshotDirectory = Environment.GetEnvironmentVariable("NARUTO_ONBOARDING_QA_DIR");
        if (!string.IsNullOrWhiteSpace(screenshotDirectory)) {
            PumpOnboarding(240);
            Directory.CreateDirectory(screenshotDirectory);
            var root = (FrameworkElement)window.FindName("WindowOverlayRoot");
            var bitmap = new RenderTargetBitmap(
                (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(screenshotDirectory, $"step-{step}-{window.Width}.png"));
            encoder.Save(file);
        }
    }

    private static void AssertOnboardingLayoutSettles(MainWindow window)
    {
        PumpOnboarding(400);
        var root = (FrameworkElement)window.FindName("WindowOverlayRoot");
        var layouts = 0;
        EventHandler onLayout = (_, _) => layouts++;
        root.LayoutUpdated += onLayout;
        try {
            PumpOnboarding(200);
            if (layouts > 5) {
                throw new InvalidOperationException("新手指引静止后不能持续触发布局。");
            }
        } finally {
            root.LayoutUpdated -= onLayout;
        }
    }

    private static void AssertOnboardingPlacement(MainWindow window)
    {
        var root = (FrameworkElement)window.FindName("WindowOverlayRoot");
        var popover = (FrameworkElement)window.FindName("OnboardingPopover");
        var bounds = popover.TransformToAncestor(root).TransformBounds(new Rect(popover.RenderSize));
        var navigation = (FrameworkElement)window.FindName("MainNavigation");
        var contentTop = navigation.TransformToAncestor(root).Transform(new System.Windows.Point()).Y;
        if (bounds.Left < 0 || bounds.Top < contentTop + 16
            || bounds.Right > root.ActualWidth || bounds.Bottom > root.ActualHeight) {
            throw new InvalidOperationException("新手指引浮层必须完整位于窗口内并避开标题栏。");
        }
    }
}
