using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Settings;
using NarutoAutoGUI.Updates;
using NarutoAutoGUI.Worker;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfSymbol = Wpf.Ui.Controls.SymbolRegular;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfCursors = System.Windows.Input.Cursors;
using WpfDataObject = System.Windows.DataObject;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using WpfSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace NarutoAutoGUI.Views;

public partial class MainWindow : FluentWindow
{
    private const int MaximumGuiLogEntries = 1000;
    private HwndSource? _modalWindowSource;
    private const string PlanItemDragDataFormat = "NarutoAutoGUI.PlanItem";

    private enum MainSection
    {
        Home,
        Help,
        Settings
    }

    private readonly AppLogger _logger;
    private readonly ApplicationSettings _settings;
    private readonly HelpPageModel _help;
    private MainSection _section;
    private readonly string _applicationDirectory;
    private readonly ChildSessionManager _sessionManager;
    private readonly WorkerCoordinator _workerCoordinator;
    private readonly RuntimeEnvironment _environment;
    private readonly Func<Task> _requestExitAsync;
    private readonly Func<Task> _endEnvironmentAsync;
    private readonly Func<string, Task> _installUpdateAsync;
    private ChildSessionSnapshot _sessionSnapshot = ChildSessionSnapshot.Empty;
    private WorkerCoordinatorSnapshot _workerSnapshot = WorkerCoordinatorSnapshot.Empty;
    private ProjectPlanModule? _projectPlan;
    private RunStartAttempt? _pendingStartAttempt;
    private ScrollViewer? _homeLogScrollViewer;
    private readonly RunLogFeed _runLog;
    private bool _allowClose;
    private PendingOperation _pendingOperation;
    private bool _environmentPreparationFailed;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _projectConfigurationValid;
    private bool _updatingOptionEditors;
    private bool _taskShelfExpanded;
    private double? _preferredRuntimeSidebarWidth;
    private double? _savedRuntimeSidebarWidth;
    private bool _updatingRuntimeSidebarWidth;
    private bool _runtimeSidebarDragging;
    private bool _runtimeSidebarKeyboardCue;
    private static readonly DependencyPropertyDescriptor RuntimeSidebarWidthProperty =
        DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition));
    // Labels of the tasks added since the add-task panel was last opened.
    private readonly List<string> _addedSinceShelfOpened = [];
    private readonly Dictionary<string, bool> _taskGroupExpanded = new(StringComparer.Ordinal);
    private string _configurationLockText = string.Empty;
    private string? _expandedTaskName;
    private PlanItemKind _expandedTaskKind;
    private string? _dragTaskName;
    private WpfPoint _dragStartPoint;
    private IInputElement? _descriptionDrawerPreviousFocus;
    private readonly List<Border> _dropIndicators = [];
    private readonly Dictionary<(PlanItemKind Kind, string Name), Border> _planItemContainers = [];
    private readonly List<(TextBlock Summary, ProjectTaskChoice Task)> _planItemSummaries = [];
    private readonly Dictionary<(PlanItemKind Kind, string Name), PlanItemStatusView> _planItemStatuses = [];
    // Only the timing text changes while a Run is active; cards are never rebuilt for it.
    private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Dictionary<(Guid, string, string), InputDraft> _invalidInputDrafts = new();
    private readonly Dictionary<(Guid, string, string), WpfTextBox> _optionInputEditors = new();
    private readonly Dictionary<string, InspectorRowView> _inspectorRows = new(StringComparer.Ordinal);
    private System.Windows.Data.Binding? _configurationEditableBinding;
    private static readonly string[] SwitchOnCaseNames = ["Yes", "Y", "On", "True", "Enable", "Enabled"];

    // Edit controls bind IsEnabled to this, so a run lock blocks edits while scrolling, expanding and help stay usable.
    private static readonly DependencyProperty ConfigurationEditableProperty = DependencyProperty.Register(
        "ConfigurationEditable", typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    private sealed record OptionInputTag(Guid ConfigurationId, string OptionName, string InputName, string Value,
        string? PatternMessage, bool Submitted = false)
    {
        internal (Guid, string, string) Key => (ConfigurationId, OptionName, InputName);
    }

    // The badge and status text of a plan card, updated in place as the Run moves on.
    private sealed record PlanItemStatusView(
        Border Badge, TextBlock Number, WpfSymbolIcon Icon, TextBlock Status, int Position);

    // The parts of a shown inspector row that change in place when a value is saved or a draft appears.
    private sealed record InspectorRowView(
        ParameterRowPanel Row, StackPanel CaseNotes, IReadOnlyDictionary<string, StackPanel> InputNotes);

    internal MainWindow(
        AppLogger logger,
        ChildSessionManager sessionManager, WorkerCoordinator workerCoordinator, RuntimeEnvironment environment,
        Func<Task> requestExitAsync, Func<Task> endEnvironmentAsync, Func<string, Task> installUpdateAsync,
        string? applicationDirectory = null, Func<CancellationToken, Task<EngineCheckResult>>? checkForUpdate = null)
    {
        InitializeComponent();
        _runLog = new RunLogFeed(LogLines, MaximumGuiLogEntries);
        DataContext = this;
        BindConfigurationEditable(NewConfigurationButton);
        BindConfigurationEditable(TaskShelfHeaderButton);
        BindConfigurationEditable(EmptyPlanAddButton);
        _logger = logger;
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _checkForUpdate = checkForUpdate ?? CheckWithEngineAsync;
        _settings = new ApplicationSettings(_applicationDirectory, logger,
            CheckUpdateFromSettingsAsync, ExportDiagnosticsFromSettingsAsync, ReplayOnboarding, OpenAfdianAsync,
            ManageChildSessionCredentialsAsync);
        SettingsView.DataContext = _settings.Page;
        HelpView.DataContext = _help = new HelpPageModel(
            _settings.ExportDiagnostics, new SettingsAction(OpenIssuesAsync), ReportCopyFailure);
        _savedRuntimeSidebarWidth = _preferredRuntimeSidebarWidth = LoadRuntimeSidebarWidth();
        _sessionManager = sessionManager;
        _workerCoordinator = workerCoordinator;
        _environment = environment;
        _requestExitAsync = requestExitAsync;
        _endEnvironmentAsync = endEnvironmentAsync;
        _installUpdateAsync = installUpdateAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        HomeLogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _sessionManager.StateChanged += OnSessionStateChanged;
        _workerCoordinator.StateChanged += OnWorkerStateChanged;
        _environment.StateChanged += OnEnvironmentStateChanged;
        _workerCoordinator.LogReceived += OnWorkerLogReceived;
        _workerSnapshot = workerCoordinator.Snapshot;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        KeyDown += MainWindow_KeyDown;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += (_, _) => UpdateConfigurationLockText();
        RuntimeSidebarWidthProperty.AddValueChanged(RuntimeSidebarColumn, RuntimeSidebarColumn_WidthChanged);
        foreach (var routedEvent in new[] {
            MouseEnterEvent, MouseLeaveEvent, MouseMoveEvent, GotKeyboardFocusEvent, LostKeyboardFocusEvent,
            PreviewKeyDownEvent, SizeChangedEvent, System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            System.Windows.Controls.Primitives.Thumb.DragCompletedEvent }) {
            RuntimeSidebarSplitter.AddHandler(routedEvent, new RoutedEventHandler(RuntimeSidebarHandle_Update), true);
        }
        HomeView.SizeChanged += (_, _) => RuntimeSidebarVisibility(_section);
        _runTimer.Tick += (_, _) => RefreshRunTiming();
        SwitchSection(MainSection.Home);
        RefreshRuntimeControls();
    }

    // Run logs newest first, each stretch from one Plan Item headed by its RunLogGroup.
    public ObservableCollection<object> LogLines { get; } = [];

    internal event EventHandler? HiddenToTray;

    internal void AllowClose() => _allowClose = true;

    internal void SetExitInProgress(bool exitInProgress)
    {
        _exitInProgress = exitInProgress;
        if (exitInProgress) {
            EndOnboarding(handled: false);
        }
        UpdateUpdaterControls();
        RefreshRuntimeControls();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await InitializeStartupAsync(RestoreExistingSessionAsync);
        try {
            AutoSessionMenuItem.IsChecked = File.Exists(AutoSessionPath)
                && File.ReadAllText(AutoSessionPath).Trim() == "true";
            if (AutoSessionMenuItem.IsChecked && CurrentControls.Action is RuntimeAction.Prepare { Enabled: true }) {
                await PrepareFromPanelAsync();
            }
        } catch (Exception exception) {
            _logger.Warn("读取自动启动分身设置失败。", exception);
        }
    }

    private async Task InitializeStartupAsync(Func<Task> restoreExistingSession)
    {
        _homeLogScrollViewer = FindVisualChild<ScrollViewer>(HomeLogListBox);
        try {
            LoadProject();
        } catch (Exception exception) {
            _logger.Warn(
                $"加载 MaaNOP Project Interface 失败。AppContext.BaseDirectory={AppContext.BaseDirectory}", exception);
            ShowProjectUnavailableState(
                "MaaNOP 项目无法加载",
                "请使用完整的 MaaNOP 发布包，确保 NarutoAutoGUI.exe 同级目录直接包含 interface.json。");
            ShowProjectValidationError(exception);
            RefreshRuntimeControls();
        }
        InitializeUpdates();
        try {
            await restoreExistingSession();
        } finally {
            _onboardingStartupCompleted = true;
            ReevaluateOnboarding();
        }
    }

    private async Task RestoreExistingSessionAsync()
    {
        var existingId = _sessionManager.DetectExistingSession();
        if (existingId is null) {
            return;
        }
        await RunOperationAsync(
            PendingOperation.RestoringSession,
            async () =>
            {
                await _sessionManager.EnsureConnectedAsync(showPreview: true);
                _logger.Info($"已有 Child Session {existingId} 恢复完成。");
            });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && IsGlobalModalOpen) {
            e.Cancel = true;
            return;
        }
        if (_allowClose) {
            _onboardingClosed = true;
            EndOnboarding(handled: false);
            RemoveModalWindowHook();
            RemoveConfigurationWheelHook();
            _runTimer.Stop();
            RuntimeSidebarWidthProperty.RemoveValueChanged(RuntimeSidebarColumn, RuntimeSidebarColumn_WidthChanged);
            _updateCancellation?.Cancel();
            _sessionManager.StateChanged -= OnSessionStateChanged;
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
            _environment.StateChanged -= OnEnvironmentStateChanged;
            _workerCoordinator.LogReceived -= OnWorkerLogReceived;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            StateChanged -= MainWindow_StateChanged;
            return;
        }

        e.Cancel = true;
        if (_exitInProgress) { return; }
        if (!_settings.CloseToTray.Value) {
            // Defer until Closing returns; the exit flow may synchronously call Close again.
            Dispatcher.InvokeAsync(async () => await _requestExitAsync());
            return;
        }
        Hide();
        _logger.Info("主窗口已隐藏到托盘。");
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RailItem { Tag: string sectionName }
            && Enum.TryParse(sectionName, ignoreCase: false, out MainSection section)) {
            SwitchSection(section);
        }
    }

    private void SwitchSection(MainSection section)
    {
        _section = section;
        CloseTaskDescriptionDrawer();
        HomeView.Visibility = section == MainSection.Home
            ? Visibility.Visible
            : Visibility.Collapsed;
        RuntimeSidebarVisibility(section);
        SettingsView.Visibility = section == MainSection.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        HelpView.Visibility = section == MainSection.Help ? Visibility.Visible : Visibility.Collapsed;
        if (section == MainSection.Settings) {
            RefreshChildSessionCredentialStatus();
        }

        HomeNavigationItem.IsActive = section == MainSection.Home;
        HelpNavigationItem.IsActive = section == MainSection.Help;
        SettingsNavigationItem.IsActive = section == MainSection.Settings;
        ReevaluateOnboarding();
    }

    private async void ShowSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(
            PendingOperation.ShowingDesktop,
            async () => await _sessionManager.EnsureConnectedAsync(showPreview: true));

    private void HideSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exitInProgress) {
            return;
        }

        try {
            _sessionManager.HidePreview();
        } catch (Exception exception) {
            HandleOperationError("隐藏子桌面失败", exception);
        }
    }

    private void TryOpenLogsDirectory()
    {
        try {
            Directory.CreateDirectory(_logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"\"{_logger.LogDirectory}\"",
                UseShellExecute = true
            });
        } catch (Exception exception) {
            _logger.Error("打开日志目录失败。", exception);
            ShowActionableError(
                "打开日志目录失败",
                exception,
                "请确认 Windows 资源管理器可用，并检查日志目录访问权限后重试。",
                offerLogDirectory: false);
        }
    }

    private void DesktopViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingActive || CurrentControls.Desktop is not { Enabled: true } desktop) {
            return;
        }
        if (desktop.Action == DesktopAction.Hide) {
            HideSessionButton_Click(sender, e);
        } else {
            ShowSessionButton_Click(sender, e);
        }
    }

    private void RuntimeSidebarVisibility(MainSection section)
    {
        var visible = section == MainSection.Home;
        RuntimeSidebar.Visibility = RuntimeSidebarSplitter.Visibility = visible
            ? Visibility.Visible : Visibility.Collapsed;
        _updatingRuntimeSidebarWidth = true;
        try {
            // ActualWidth can exceed the allotted space when the columns' minimum sizes no longer fit.
            var homeWidth = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(HomeView).Width;
            var minimum = visible ? 340 + RuntimeSidebar.Margin.Right : 0;
            var maximum = visible ? Math.Max(minimum, Math.Min(540 + RuntimeSidebar.Margin.Right,
                homeWidth - HomeView.ColumnDefinitions[0].MinWidth)) : 0;
            RuntimeSidebarColumn.MinWidth = minimum;
            RuntimeSidebarColumn.MaxWidth = maximum;
            var preferred = _preferredRuntimeSidebarWidth
                ?? SidebarWidth(homeWidth) + RuntimeSidebar.Margin.Right;
            RuntimeSidebarColumn.Width = new GridLength(Math.Clamp(preferred, minimum, maximum));
        } finally {
            _updatingRuntimeSidebarWidth = false;
        }
    }

    private void RuntimeSidebarColumn_WidthChanged(object? sender, EventArgs e)
    {
        // Observe native mouse, keyboard and automation resizing; automatic layout never replaces the preference.
        if (!_updatingRuntimeSidebarWidth) {
            _preferredRuntimeSidebarWidth = RuntimeSidebarColumn.Width.Value;
        }
    }

    // The GridSplitter does all resizing; this only places its handle and picks the visual state.
    private void RuntimeSidebarHandle_Update(object sender, RoutedEventArgs e)
    {
        var splitter = RuntimeSidebarSplitter;
        if (e.RoutedEvent == System.Windows.Controls.Primitives.Thumb.DragStartedEvent) {
            _runtimeSidebarDragging = true;
            _runtimeSidebarKeyboardCue = false;
        } else if (e.RoutedEvent == System.Windows.Controls.Primitives.Thumb.DragCompletedEvent) {
            _runtimeSidebarDragging = false;
            SaveRuntimeSidebarWidth();
        } else if (e is KeyboardEventArgs) {
            if (e.RoutedEvent == LostKeyboardFocusEvent) {
                // Arrow-key resizing has no drag end; leaving the splitter finishes it.
                SaveRuntimeSidebarWidth();
            }
            // Like a focus rectangle, focus shows the handle only when it came from the keyboard.
            _runtimeSidebarKeyboardCue = e.RoutedEvent != LostKeyboardFocusEvent
                && InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        }
        var pointer = _runtimeSidebarDragging || splitter.IsMouseOver;
        var keyboard = _runtimeSidebarKeyboardCue && splitter.IsKeyboardFocused;
        if ((pointer || keyboard) && splitter.Template.FindName("Handle", splitter) is FrameworkElement handle
            && splitter.Template.FindName("Tether", splitter) is FrameworkElement tether) {
            // Follow the pointer, kept 24 DIP from the console's ends so it clears the rounded corners.
            var y = Math.Clamp(pointer ? Mouse.GetPosition(splitter).Y : splitter.ActualHeight / 2,
                24, Math.Max(24, splitter.ActualHeight - 24));
            handle.Margin = new Thickness(0, y - handle.Height / 2, 0, 0);
            // The edge segment's clip starts 10 DIP down, below the corner radius.
            tether.Margin = new Thickness(0, y - 10 - tether.Height / 2, 0, 0);
        }
        VisualStateManager.GoToState(splitter, _runtimeSidebarDragging ? "Dragging"
            : splitter.IsMouseOver ? "Hover" : keyboard ? "Keyboard" : "Hidden", true);
    }

    private string RuntimeSidebarWidthPath =>
        Path.Combine(_applicationDirectory, "config", "runtime-sidebar-width.txt");

    // The saved width is the user's preference; layout still clamps it to the space each window size allows.
    private double? LoadRuntimeSidebarWidth()
    {
        try {
            if (!File.Exists(RuntimeSidebarWidthPath)) {
                return null;
            }
            var text = File.ReadAllText(RuntimeSidebarWidthPath).Trim();
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var width)
                || !double.IsFinite(width) || width <= 0) {
                throw new InvalidDataException($"侧栏宽度必须为正数：{text}");
            }
            return width;
        } catch (Exception exception) {
            _logger.Warn("读取运行控制栏宽度失败，已使用默认宽度。", exception);
            return null;
        }
    }

    private void SaveRuntimeSidebarWidth()
    {
        if (_preferredRuntimeSidebarWidth is not { } width || width == _savedRuntimeSidebarWidth) {
            return;
        }
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimeSidebarWidthPath)!);
            File.WriteAllText(RuntimeSidebarWidthPath,
                width.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            _savedRuntimeSidebarWidth = width;
        } catch (Exception exception) {
            _logger.Warn("保存运行控制栏宽度失败。", exception);
        }
    }

    // The console takes about a third of the home page, bounded so neither it nor the task area gets cramped.
    internal static double SidebarWidth(double homeWidth) => Math.Clamp(Math.Round(homeWidth * 0.35), 340, 540);

    private void RuntimeMoreButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeMoreMenu.PlacementTarget = RuntimeMoreButton;
        RuntimeMoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        RuntimeMoreMenu.IsOpen = true;
    }

    private void OpenLogsMenuItem_Click(object sender, RoutedEventArgs e) => TryOpenLogsDirectory();

    // The same routine as the tray's "结束桌面分身", including its confirmation.
    private async void EndEnvironmentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (EndEnvironmentBlocker(CurrentControls) is null) {
            await _endEnvironmentAsync();
        }
    }

    // Why the desktop clone cannot be ended from the menu now, or null when it can.
    internal static string? EndEnvironmentBlocker(RuntimeControlState state) =>
        state.Action is RuntimeAction.Recover { Enabled: true } ? null
        : !state.CommandsAvailable ? "处理中"
        : !state.Desktop.Enabled ? "未创建" : null;

    private string AutoSessionPath => Path.Combine(_applicationDirectory, "config", "auto-session.txt");

    private void AutoSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoSessionPath)!);
            File.WriteAllText(AutoSessionPath, AutoSessionMenuItem.IsChecked ? "true" : "false");
        } catch (Exception exception) {
            AutoSessionMenuItem.IsChecked = !AutoSessionMenuItem.IsChecked;
            ShowActionableError("保存自动启动分身设置失败", exception, "请检查配置目录写入权限。", offerLogDirectory: false);
        }
    }

    private async void PrepareAndRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitFocusedConfigurationInput() || RevealInvalidInputDraft() || OperationOrExitInProgress) {
            return;
        }
        await PrepareFromPanelAsync();
        if (!_environmentPreparationFailed && CurrentControls.Action is RuntimeAction.Start { Enabled: true }) {
            StartRunButton_Click(sender, e);
        }
    }

    private async void PrepareEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        await PrepareFromPanelAsync();
    }

    private async Task PrepareFromPanelAsync()
    {
        if (OperationOrExitInProgress) {
            return;
        }
        _environmentPreparationFailed = false;
        await RunOperationAsync(
            PendingOperation.PreparingEnvironment,
            async () =>
            {
                try {
                    LoadProject();
                    await _environment.PrepareAsync(RequireProject(), _settings.CreateFrameworkOptions);
                } catch {
                    _environmentPreparationFailed = true;
                    throw;
                }
            });
    }

    // RuntimeControls chooses what Retry means; this handler only carries it out.
    private void RetryRuntimeHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is RuntimeAction.Retry { Target: RetryTarget.Start }) {
            StartRunButton_Click(sender, e);
        } else {
            PrepareAndRunButton_Click(sender, e);
        }
    }

    // The same routine as the tray's "结束桌面分身", including its confirmation.
    private async void RecoverEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is RuntimeAction.Recover { Enabled: true }) {
            await _endEnvironmentAsync();
        }
    }

    private async void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitFocusedConfigurationInput() || RevealInvalidInputDraft()) {
            return;
        }
        if (CurrentControls.Action is not (RuntimeAction.Start { Enabled: true }
            or RuntimeAction.Retry { Target: RetryTarget.Start, Enabled: true })) {
            return;
        }
        await RunOperationAsync(
            PendingOperation.StartingRun,
            async () =>
            {
                // The operation may wait behind another one, so the session is checked again when it runs.
                if (_sessionSnapshot.State is not (ChildSessionState.ConnectedVisible
                    or ChildSessionState.ConnectedHidden)) {
                    throw new InvalidOperationException("Child Session 尚未连接，当前不能开始任务。 ");
                }
                var project = RequireProject();
                if (!_projectConfigurationValid) {
                    throw new InvalidOperationException(
                        "当前 MaaNOP Config 尚未通过正式 PI Resolver 校验。 ");
                }
                _pendingStartAttempt ??= project.CreateRunStartAttempt();
                var response = await _workerCoordinator.StartRunAsync(_pendingStartAttempt);
                if (response.Disposition is not ("accepted" or "already_accepted")) {
                    throw new InvalidDataException($"未知 run.start disposition：{response.Disposition}。 ");
                }
                _logger.Info(
                    $"run.start {response.Disposition}：runId={_pendingStartAttempt.RunId:D}；"
                    + $"planDigest={_pendingStartAttempt.PlanDigest}。 ");
                _pendingStartAttempt = null;
            });
    }

    private async void StopRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentControls.Action is not RuntimeAction.Stop { Enabled: true }) {
            return;
        }
        await RunOperationAsync(
            PendingOperation.StoppingRun,
            async () =>
            {
                var activeRun = _workerSnapshot.WorkerSnapshot?.ActiveRun
                                ?? throw new InvalidOperationException("Worker 当前没有 active Run。 ");
                if (!RuntimeControls.IsStoppable(activeRun)) {
                    throw new InvalidOperationException("当前执行计划尚未进入可停止状态。 ");
                }
                var response = await _workerCoordinator.StopRunAsync(activeRun.RunId);
                if (response.Disposition != "stop_requested") {
                    throw new InvalidDataException(
                        $"首次 run.stop 未返回 stop_requested：{response.Disposition}。 ");
                }
                _logger.Info($"run.stop 已确认 stop_requested：runId={activeRun.RunId:D}。 ");
            });
    }

    private void TaskShelfHeaderButton_Click(object sender, RoutedEventArgs e)
        => SetTaskShelfExpanded(!_taskShelfExpanded);

    // The panel stays open while several tasks are added; each opening or configuration counts its additions afresh.
    private void SetTaskShelfExpanded(bool expanded)
    {
        if (!expanded) {
            TaskSearchBox.Clear();
        } else if (!_taskShelfExpanded) {
            _addedSinceShelfOpened.Clear();
            UpdateAddedTasks();
        }
        _taskShelfExpanded = expanded;
        TaskShelfContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        EmptyPlanAddButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        TaskShelfHeaderIcon.Symbol = expanded ? WpfSymbolRegular.ChevronUp16 : WpfSymbolRegular.Add16;
        TaskShelfHeaderText.Text = expanded ? "完成添加" : "添加任务";
        AutomationProperties.SetName(TaskShelfHeaderButton, TaskShelfHeaderText.Text);
        AutomationProperties.SetHelpText(TaskShelfHeaderButton, expanded ? "可用任务已展开" : "可用任务已收起");
    }

    private void UpdateAddedTasks()
    {
        AddedTasksBadge.Visibility = _addedSinceShelfOpened.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AddedTasksText.Text = $"已添加 {_addedSinceShelfOpened.Count} 项";
        AddedTasksBadge.ToolTip = string.Join("、", _addedSinceShelfOpened);
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CurrentControls.ConfigurationEditable || sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            var project = RequireProject();
            if (!(task.Kind == PlanItemKind.Pretask ? project.AddPretask(task.Name) : project.AddTask(task.Name))) {
                return;
            }
            _pendingStartAttempt = null;
            _addedSinceShelfOpened.Add(task.Label);
            UpdateAddedTasks();
            RenderTaskPlan();
            // The plan keeps its shape: the new card is shown and highlighted rather than expanded.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LocatePlanItem(task));
            _logger.Info($"已添加执行计划任务：{task.Name}。 ");
        } catch (Exception exception) {
            HandleProjectEditError("添加执行计划任务失败", exception);
        }
    }

    private void LocatePlanItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: ProjectTaskChoice task }) {
            LocatePlanItem(task);
        }
    }

    private void LocatePlanItem(ProjectTaskChoice task)
    {
        if (_planItemContainers.TryGetValue((task.Kind, task.Name), out var card)) {
            BringIntoPlanView(card);
            FlashPlanItem(card);
        }
    }

    // Pads by the edge fade so the target never stops under it, and caps a tall card so its header stays visible.
    private void BringIntoPlanView(FrameworkElement element)
    {
        var fade = (double)FindResource("Height.ScrollFade");
        var height = Math.Min(element.ActualHeight, Math.Max(0, PlanScroll.ViewportHeight - fade * 2));
        element.BringIntoView(new Rect(0, -fade, element.ActualWidth, height + fade * 2));
    }

    // Tints the located card with the task accent for a moment, then fades back to the colours of its style.
    private void FlashPlanItem(Border card)
    {
        if (!SystemParameters.ClientAreaAnimation) {
            return;
        }
        var restoreBorder = ((SolidColorBrush)card.BorderBrush).Color;
        var restoreSurface = ((SolidColorBrush)card.Background).Color;
        var border = new SolidColorBrush(((SolidColorBrush)FindResource("Brush.TasksAccent")).Color);
        var surface = new SolidColorBrush(((SolidColorBrush)FindResource("Brush.TasksAccent.Surface")).Color);
        card.BorderBrush = border;
        card.Background = surface;
        var delay = TimeSpan.FromMilliseconds(700);
        var duration = TimeSpan.FromMilliseconds(600);
        var borderFade = new ColorAnimation(restoreBorder, duration) { BeginTime = delay };
        borderFade.Completed += (_, _) => {
            card.ClearValue(Border.BorderBrushProperty);
            card.ClearValue(Border.BackgroundProperty);
        };
        border.BeginAnimation(SolidColorBrush.ColorProperty, borderFade);
        surface.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(restoreSurface, duration) { BeginTime = delay });
    }

    // A field holding an invalid, unsaved value blocks Start: show that field instead of running the saved value.
    private bool RevealInvalidInputDraft()
    {
        if (_projectPlan is not { } project
            || !_invalidInputDrafts.Keys.Any(key => key.Item1 == project.ActiveConfigurationId)) {
            return false;
        }
        var match = project.SelectedItems.OrderBy(task => IsExpanded(task) ? 0 : 1)
            .SelectMany(task => DeriveInspectorRows(task.Name, GetConfigurationOrEmpty(task.Name, task.Kind),
                shared: false, task.Kind).SelectMany(row => row.Inputs.Select(input => (Task: task,
                    Option: row.Option.Name, Key: (project.ActiveConfigurationId, row.Option.Name, input.Input.Name)))))
            .FirstOrDefault(item => _invalidInputDrafts.ContainsKey(item.Key));
        if (match.Task is null) {
            return false;
        }
        if (!IsExpanded(match.Task) || !_optionInputEditors.ContainsKey(match.Key)) {
            _expandedTaskName = match.Task.Name;
            _expandedTaskKind = match.Task.Kind;
            RenderTaskPlan();
        }
        if (_optionInputEditors.TryGetValue(match.Key, out var editor)) {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => {
                BringIntoPlanView(editor);
                editor.Focus();
            });
        }
        _logger.Info($"参数 {match.Option} 未通过校验，已阻止开始任务。 ");
        return true;
    }

    // The header is the only expansion toggle; a task without parameters has nothing to expand.
    private void PlanItemHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ProjectTaskChoice task }
            || !IsExpanded(task) && !HasParameters(GetConfigurationOrEmpty(task.Name, task.Kind))) {
            return;
        }
        _expandedTaskName = IsExpanded(task) ? null : task.Name;
        _expandedTaskKind = task.Kind;
        RenderTaskPlan();
    }

    private void RemovePlanItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CurrentControls.ConfigurationEditable || sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            var project = RequireProject();
            var removed = task.Kind == PlanItemKind.Pretask
                ? project.RemovePretask(task.Name) : project.RemoveTask(task.Name);
            if (!removed) {
                return;
            }
            if (IsExpanded(task)) {
                _expandedTaskName = null;
            }
            CloseTaskDescriptionDrawer();
            _pendingStartAttempt = null;
            RenderTaskPlan();
            _logger.Info($"已从执行计划移除任务：{task.Name}。 ");
        } catch (Exception exception) {
            HandleProjectEditError("移除执行计划任务失败", exception);
        }
    }

    private void TaskDescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: ProjectTaskChoice task }) {
            OpenTaskDescriptionDrawer(task);
        }
    }

    private void TaskDescriptionCloseButton_Click(object sender, RoutedEventArgs e) => CloseTaskDescriptionDrawer();

    private void TaskDescriptionScrim_Click(object sender, RoutedEventArgs e) => CloseTaskDescriptionDrawer();

    private void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (IsOnboardingVisible) {
            if (e.Key == Key.Escape) {
                EndOnboarding(handled: true);
                e.Handled = true;
            } else if (!(e.Key == Key.System && e.SystemKey == Key.F4)
                && e.Key is not (Key.Tab or Key.Enter or Key.Space)) {
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape && UpdateOverlay.Visibility == Visibility.Visible) {
            CloseUpdate_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && TaskDescriptionOverlay.Visibility == Visibility.Visible) {
            CloseTaskDescriptionDrawer();
            e.Handled = true;
        }
    }

    private void OpenTaskDescriptionDrawer(ProjectTaskChoice task)
    {
        if (_onboardingActive) {
            return;
        }
        _descriptionDrawerPreviousFocus = Keyboard.FocusedElement;
        TaskDescriptionDrawerTitle.Text = task.Label;
        TaskDescriptionViewer.Document = MarkdownDocument.Create(task.Description, OpenTaskDescriptionLink);
        FindVisualChild<ScrollViewer>(TaskDescriptionViewer)?.ScrollToTop();
        TaskDescriptionOverlay.Visibility = Visibility.Visible;
        TaskDescriptionCloseButton.Focus();
    }

    private void CloseTaskDescriptionDrawer()
    {
        if (TaskDescriptionOverlay.Visibility != Visibility.Visible) {
            return;
        }
        TaskDescriptionOverlay.Visibility = Visibility.Collapsed;
        if (_descriptionDrawerPreviousFocus is UIElement element && element.IsVisible) {
            element.Focus();
        }
        _descriptionDrawerPreviousFocus = null;
        ReevaluateOnboarding();
    }

    private void OptionInputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox editor) {
            _ = CommitOptionInput(editor);
        }
    }

    // Enter submits like losing focus; Esc abandons an unsaved or invalid edit and puts the saved value back.
    private void OptionInputTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is WpfTextBox { Tag: OptionInputTag } submitted) {
            _ = CommitOptionInput(submitted);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape || sender is not WpfTextBox { Tag: OptionInputTag tag } editor
            || editor.Text == tag.Value && !_invalidInputDrafts.ContainsKey(tag.Key)) {
            return;
        }
        _invalidInputDrafts.Remove(tag.Key);
        editor.Text = tag.Value;
        editor.SelectAll();
        ShowOptionInputError(editor, null);
        RefreshParameterDisplays();
        e.Handled = true;
    }

    // The message itself is shown under the field by the inspector row; the editor only changes its border.
    private void ShowOptionInputError(WpfTextBox editor, string? message)
    {
        if (message is null) {
            editor.ClearValue(BorderBrushProperty);
        } else {
            editor.BorderBrush = (WpfBrush)FindResource("Brush.Error");
        }
        AutomationProperties.SetItemStatus(editor, message ?? string.Empty);
    }

    private void SaveSelectedCase(Guid configurationId, string optionName, string caseName)
    {
        if (_updatingOptionEditors || !CurrentControls.ConfigurationEditable) {
            return;
        }

        try {
            var before = GetExpandedOptionNames();
            _ = RequireProject().SetSelectedCase(configurationId, optionName, caseName);
            _pendingStartAttempt = null;
            RefreshAfterOptionSaved(!before.SequenceEqual(GetExpandedOptionNames()));
            _logger.Info($"已保存 MaaNOP explicit case：option={optionName}。 ");
        } catch (Exception exception) {
            HandleOperationError("保存 MaaNOP select/switch option 失败", exception);
            TryRenderTaskPlan();
            ShowProjectValidationError(exception);
        }
    }

    private void TryRenderTaskPlan()
    {
        try {
            RenderTaskPlan();
        } catch (Exception exception) {
            _projectConfigurationValid = false;
            ShowProjectValidationError(exception);
            _logger.Warn("刷新 MaaNOP option 编辑器失败。", exception);
            RefreshRuntimeControls();
        }
    }

    private void RenderTaskPlan()
    {
        var project = RequireProject();
        _updatingOptionEditors = true;
        try {
            RenderConfigurationTabs();
            RenderAvailableTaskShelf(project);
            RenderPlanItems(project);
            RefreshConfigurationValidity(project);
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private void RefreshConfigurationValidity(ProjectPlanModule project)
    {
        try {
            project.ValidateConfiguration();
            _projectConfigurationValid = true;
            ProjectValidationText.Text = project.LoadWarning ?? string.Empty;
            ProjectValidationBorder.Visibility = project.LoadWarning is null
                ? Visibility.Collapsed : Visibility.Visible;
        } catch (Exception exception) {
            _projectConfigurationValid = false;
            ShowProjectValidationError(exception);
        }
        RefreshRuntimeControls();
    }

    // Option edits never change the task shelf or tabs. Plan cards are rebuilt only when nested options appear or
    // vanish; otherwise the edited control stays in place and keeps its animation and focus.
    private void RefreshAfterOptionSaved(bool rebuildCards)
    {
        var project = RequireProject();
        if (rebuildCards) {
            _updatingOptionEditors = true;
            try {
                RenderPlanItems(project);
            } finally {
                _updatingOptionEditors = false;
            }
        } else {
            RefreshParameterDisplays();
        }
        RefreshConfigurationValidity(project);
    }

    private string[] GetExpandedOptionNames() => _expandedTaskName is not { } taskName ? []
        : DeriveInspectorRows(taskName, GetConfigurationOrEmpty(taskName, _expandedTaskKind),
            shared: false, _expandedTaskKind)
            .Select(row => row.Option.Name).ToArray();

    private void RenderAvailableTaskShelf(ProjectPlanModule project)
    {
        var query = TaskSearchBox.Text.Trim();
        bool Matches(ProjectTaskChoice task) => task.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || task.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
        var tasks = project.Tasks.Where(Matches).ToArray();
        var pretasks = project.Pretasks.Where(Matches).ToArray();
        var total = project.Tasks.Count + project.Pretasks.Count;
        AvailableTaskCountText.Text = query.Length == 0
            ? total.ToString() : $"{tasks.Length + pretasks.Length} / {total}";
        AvailableTasksPanel.Children.Clear();
        void AddHeading(string text, double top) => AvailableTasksPanel.Children.Add(new TextBlock {
            Text = text, Margin = new Thickness(2, top, 0, 2), FontWeight = FontWeights.SemiBold
        });
        if (pretasks.Length != 0) {
            AddHeading("前置任务", 8);
            AvailableTasksPanel.Children.Add(CreateTaskChipPanel(project, pretasks));
            if (tasks.Length != 0) {
                AddHeading("普通任务", 12);
            }
        }
        if (tasks.Length == 0 && pretasks.Length == 0) {
            AvailableTasksPanel.Children.Add(new TextBlock {
                Text = "没有匹配的任务", Margin = new Thickness(2, 8, 0, 12),
                Style = (Style)FindResource("SecondaryTextStyle")
            });
        } else if (project.Groups.Count == 0) {
            AvailableTasksPanel.Children.Add(CreateTaskChipPanel(project, tasks));
        } else {
            var knownGroups = project.Groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var group in project.Groups) {
                AddTaskGroup(project, group.Name, group.Label, group.DefaultExpand,
                    tasks.Where(task => task.Groups.Contains(group.Name, StringComparer.Ordinal)).ToArray(),
                    query.Length != 0);
            }
            AddTaskGroup(project, string.Empty, "未分组", false,
                tasks.Where(task => !task.Groups.Any(knownGroups.Contains)).ToArray(), query.Length != 0);
        }
    }

    private void AddTaskGroup(ProjectPlanModule project, string name, string label, bool defaultExpand,
        IReadOnlyList<ProjectTaskChoice> tasks, bool searching)
    {
        if (tasks.Count == 0) {
            return;
        }
        var header = new StackPanel { Orientation = WpfOrientation.Horizontal };
        header.Children.Add(new TextBlock {
            Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock {
            Text = tasks.Count.ToString(), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("MutedTextStyle")
        });
        var section = new Expander {
            Header = header, Tag = name, Margin = new Thickness(0, 0, 0, 2),
            Style = (Style)FindResource("TaskGroupExpanderStyle"),
            IsExpanded = searching || _taskGroupExpanded.GetValueOrDefault(name, defaultExpand),
            Content = CreateTaskChipPanel(project, tasks),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, $"{label}，{tasks.Count} 个任务");
        if (!searching) {
            section.Expanded += (_, _) => _taskGroupExpanded[name] = true;
            section.Collapsed += (_, _) => _taskGroupExpanded[name] = false;
        }
        AvailableTasksPanel.Children.Add(section);
    }

    private WrapPanel CreateTaskChipPanel(ProjectPlanModule project, IEnumerable<ProjectTaskChoice> tasks)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.SizeChanged += (_, args) => {
            foreach (WpfButton button in panel.Children) {
                button.MaxWidth = Math.Max(0, args.NewSize.Width - 8);
            }
        };
        foreach (var task in tasks) {
            var selected = task.Kind == PlanItemKind.Pretask ? project.SelectedPretaskNames : project.SelectedTaskNames;
            var added = selected.Contains(task.Name, StringComparer.Ordinal);
            var label = new TextBlock {
                Text = task.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            };
            var icon = CreateSymbolIcon(added ? WpfSymbolRegular.Checkmark16 : WpfSymbolRegular.Add16);
            icon.Margin = new Thickness(8, 0, 0, 0);
            icon.Foreground = (WpfBrush)FindResource("Brush.TasksAccent");
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Right);
            content.Children.Add(icon);
            content.Children.Add(label);
            var button = new WpfButton {
                Content = content,
                Tag = task,
                Style = (Style)FindResource(added ? "TaskChipAddedButtonStyle" : "TaskChipButtonStyle")
            };
            // The implicit TextBlock style would pin the label colour; follow the chip style instead.
            label.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
            if (added) {
                // A planned task is not added twice; its chip points to the plan card instead of doing nothing.
                button.ToolTip = "已在执行计划中，点击定位";
                AutomationProperties.SetName(button, $"已添加：{task.Label}，定位到执行计划");
                button.Click += LocatePlanItemButton_Click;
            } else {
                BindConfigurationEditable(button);
                AutomationProperties.SetName(button, $"添加任务：{task.Label}");
                button.Click += AddTaskButton_Click;
            }
            panel.Children.Add(button);
        }
        return panel;
    }

    // Esc first clears the search, then closes the panel, wherever focus is. It arrives here only when no editor
    // used it, so reverting a parameter draft or cancelling a rename keeps its own Esc.
    private void MainWindow_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_taskShelfExpanded || HomeView.Visibility != Visibility.Visible) {
            return;
        }
        if (TaskSearchBox.Text.Length != 0) {
            TaskSearchBox.Clear();
        } else {
            SetTaskShelfExpanded(false);
            TaskShelfHeaderButton.Focus();
        }
        e.Handled = true;
    }

    private void TaskSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_projectPlan is not null) {
            RenderAvailableTaskShelf(_projectPlan);
            AvailableTasksScroll.ScrollToTop();
        }
    }

    private void TaskWorkspacePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TaskShelfContent.MaxHeight = Math.Min(340, Math.Max(0, e.NewSize.Height * 0.44));
    }

    private void RenderPlanItems(ProjectPlanModule project)
    {
        PlanItemsPanel.Children.Clear();
        _dropIndicators.Clear();
        _planItemContainers.Clear();
        _planItemSummaries.Clear();
        _taskDescriptionButtons.Clear();
        _optionInputEditors.Clear();
        _inspectorRows.Clear();
        _planItemStatuses.Clear();
        var items = project.SelectedItems;
        EmptyPlanPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanCountText.Text = $"{items.Count} 项";
        EmptyPlanText.Text = $"从 {project.Tasks.Count + project.Pretasks.Count} 个可用任务中选择要执行的任务";

        for (var position = 0; position < items.Count; position++) {
            var task = items[position];
            if (task.Kind == PlanItemKind.MaaTask) {
                AddDropIndicator();
            }
            var container = CreatePlanItem(task, position, IsExpanded(task));
            _planItemContainers.Add((task.Kind, task.Name), container);
            PlanItemsPanel.Children.Add(container);
        }
        AddDropIndicator();
        ApplyPlanItemStatuses(CurrentRunProgress);
    }

    private Task OpenAfdianAsync() =>
        OpenWebPageAsync(_settings.OpenAfdian, "https://afdian.com/a/archersore", "爱发电", "爱发电赞助页面");

    private Task OpenIssuesAsync() => OpenWebPageAsync(
        _help.OpenIssues, "https://github.com/ArcherSore/MaaNOP/issues", "GitHub Issues", "GitHub Issues 页面");

    private Task OpenWebPageAsync(SettingsAction action, string address, string site, string page)
    {
        try {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            action.Status = string.Empty;
        } catch (Exception exception) {
            action.Status = $"无法打开{site}，请检查默认浏览器设置。";
            _logger.Warn($"打开{page}失败。", exception);
            WpfMessageBox.Show(this, $"请手动访问：{address}\n\n按 Ctrl+C 可复制此提示中的网址。",
                $"无法打开{site}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return Task.CompletedTask;
    }

    private void ReportCopyFailure(Exception exception)
    {
        _logger.Warn("复制 QQ 号失败。", exception);
        WpfMessageBox.Show(this, $"剪贴板暂时不可用，请手动记下 QQ 号：{_help.Qq}",
            "无法复制 QQ 号", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenTaskDescriptionLink(Uri uri)
    {
        try {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        } catch (Exception exception) {
            _logger.Warn("打开任务说明链接失败。", exception);
            ShowActionableError("无法打开链接", exception, "请检查默认浏览器设置。", offerLogDirectory: false);
        }
    }

    private bool IsExpanded(ProjectTaskChoice task) =>
        _expandedTaskName == task.Name && _expandedTaskKind == task.Kind;

    private ProjectConfigurationView GetConfigurationOrEmpty(string taskName, PlanItemKind kind)
    {
        try {
            return RequireProject().GetConfiguration(taskName, kind);
        } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) {
            return new ProjectConfigurationView([], []);
        }
    }

    private static bool HasParameters(ProjectConfigurationView configuration) =>
        configuration.GlobalOptions.Count + configuration.TaskOptions.Count != 0;

    private Border CreatePlanItem(ProjectTaskChoice task, int position, bool expanded)
    {
        var configuration = GetConfigurationOrEmpty(task.Name, task.Kind);
        var hasParameters = HasParameters(configuration);
        expanded &= hasParameters;
        var container = new Border {
            Tag = task,
            Style = (Style)FindResource(expanded ? "PlanItemExpandedStyle" : "PlanItemStyle")
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (expanded) {
            var accentLine = new Border {
                Width = 3,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                Margin = new Thickness(1, 6, 0, 6),
                CornerRadius = new CornerRadius(1.5),
                Background = (WpfBrush)FindResource("Brush.TasksAccent")
            };
            Grid.SetRowSpan(accentLine, 2);
            layout.Children.Add(accentLine);
        }

        layout.Children.Add(CreatePlanItemHeader(task, position, expanded, hasParameters, configuration));

        if (expanded) {
            var editor = new Border {
                Style = (Style)FindResource("ParameterEditorStyle"),
                Child = CreateParameterInspector(task.Name, configuration, task.Kind)
            };
            // A wide card lines the inspector up with the task label; a narrow one gives it the full width.
            container.SizeChanged += (_, args) =>
                editor.Padding = new Thickness(args.NewSize.Width >= 600 ? 44 : 14, 12, 14, 14);
            Grid.SetRow(editor, 1);
            layout.Children.Add(editor);
        }
        container.Child = layout;
        return container;
    }

    private Grid CreatePlanItemHeader(ProjectTaskChoice task, int position, bool expanded, bool hasParameters,
        ProjectConfigurationView configuration)
    {
        var header = new Grid {
            MinHeight = 44,
            Margin = new Thickness(expanded ? 6 : 4, 0, 4, 0)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dragHandle = CreateIconButton(WpfSymbolRegular.ReOrderDotsVertical20, "拖动以调整顺序", "拖动以调整顺序");
        dragHandle.Tag = task;
        if (task.Kind == PlanItemKind.Pretask) {
            dragHandle.Visibility = Visibility.Hidden;
            dragHandle.IsHitTestVisible = false;
        } else {
            dragHandle.Cursor = WpfCursors.SizeAll;
            dragHandle.PreviewMouseLeftButtonDown += PlanDragHandle_PreviewMouseLeftButtonDown;
            dragHandle.PreviewMouseMove += PlanDragHandle_PreviewMouseMove;
            BindConfigurationEditable(dragHandle);
        }
        header.Children.Add(dragHandle);
        header.Children.Add(CreatePlanItemBadge(task, position));

        var label = new TextBlock {
            Text = task.Kind == PlanItemKind.Pretask ? $"前置 · {task.Label}" : task.Label,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var showSummary = !expanded && hasParameters;
        if (showSummary) {
            label.TextWrapping = TextWrapping.NoWrap;
            title.Children.Add(label);
            var separator = new Border {
                Width = 1,
                Height = 16,
                Margin = new Thickness(10, 0, 10, 0),
                Background = (WpfBrush)FindResource("Brush.Border.Subtle"),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            Grid.SetColumn(separator, 1);
            title.Children.Add(separator);
            var summaryText = new TextBlock {
                FontWeight = FontWeights.Normal,
                Foreground = (WpfBrush)FindResource("Brush.Text.Secondary"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _planItemSummaries.Add((summaryText, task));
            FillParameterSummary(summaryText, task.Name, configuration, task.Kind);
            Grid.SetColumn(summaryText, 2);
            title.Children.Add(summaryText);
        }
        var main = new WpfButton {
            Tag = task,
            Content = showSummary ? title : label,
            Style = (Style)FindResource("PlanHeaderButtonStyle")
        };
        AutomationProperties.SetName(main,
            $"{task.Label}，{(hasParameters ? (expanded ? "已展开" : "已折叠") : "没有可配置的参数")}");
        AutomationProperties.SetHelpText(main, hasParameters ? (expanded ? "点击收起参数" : "点击展开参数") : string.Empty);
        main.Click += PlanItemHeader_Click;
        Grid.SetColumn(main, 2);
        header.Children.Add(main);
        var status = new TextBlock {
            FontSize = 12, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Grid.SetColumn(status, 3);
        header.Children.Add(status);
        var badge = _planItemStatuses[(task.Kind, task.Name)];
        _planItemStatuses[(task.Kind, task.Name)] = badge with { Status = status };

        if (!string.IsNullOrWhiteSpace(task.Description)) {
            var information = CreateIconButton(WpfSymbolRegular.Info16, $"查看 {task.Label} 的任务描述", "查看任务说明");
            information.Tag = task;
            information.Click += TaskDescriptionButton_Click;
            _taskDescriptionButtons[(task.Kind, task.Name)] = information;
            Grid.SetColumn(information, 4);
            header.Children.Add(information);
        }

        var remove = CreateIconButton(WpfSymbolRegular.Dismiss16, $"从执行计划移除 {task.Label}", "从执行计划移除");
        remove.Style = (Style)FindResource("TaskRemoveButtonStyle");
        remove.Tag = task;
        remove.Click += RemovePlanItemButton_Click;
        BindConfigurationEditable(remove);
        Grid.SetColumn(remove, 5);
        header.Children.Add(remove);
        return header;
    }

    // The plan position, replaced by the item's state while a Run describes this plan.
    private Border CreatePlanItemBadge(ProjectTaskChoice task, int position)
    {
        var number = new TextBlock {
            Text = (position + 1).ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = CreateSymbolIcon(WpfSymbolRegular.Subtract16);
        icon.Width = icon.Height = icon.FontSize = 14;
        icon.Visibility = Visibility.Collapsed;
        var badge = new Border {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = new Grid { Children = { number, icon } }
        };
        Grid.SetColumn(badge, 1);
        _planItemStatuses[(task.Kind, task.Name)] = new PlanItemStatusView(
            badge, number, icon, new TextBlock(), position);
        return badge;
    }

    // Drafts are those of the active configuration; sharing is only worked out for the expanded card.
    private IReadOnlyList<InspectorRow> DeriveInspectorRows(
        string taskName, ProjectConfigurationView configuration, bool shared, PlanItemKind kind)
    {
        var project = RequireProject();
        var drafts = _invalidInputDrafts.Where(pair => pair.Key.Item1 == project.ActiveConfigurationId)
            .ToDictionary(pair => (pair.Key.Item2, pair.Key.Item3), pair => pair.Value);
        (string, ProjectConfigurationView)[] others = !shared ? [] : project.SelectedItems
            .Where(task => task.Name != taskName || task.Kind != kind)
            .Select(task => (task.Label, GetConfigurationOrEmpty(task.Name, task.Kind))).ToArray();
        return ParameterInspector.Derive(configuration, drafts, others);
    }

    // An unfixed invalid draft replaces the saved value in red, so folded cards agree with the field and Start block.
    private void FillParameterSummary(TextBlock summary, string taskName, ProjectConfigurationView configuration,
        PlanItemKind kind)
    {
        summary.Inlines.Clear();
        foreach (var row in DeriveInspectorRows(taskName, configuration, shared: false, kind)) {
            var parts = row.Kind == ProjectOptionKind.Input
                ? row.Inputs.Select(input => (input.Label, Value: input.DisplayValue, Invalid: input.Draft is not null))
                : [(row.Label, Value: row.SelectedCase?.Label ?? string.Empty, Invalid: false)];
            foreach (var (label, value, invalid) in parts) {
                if (summary.Inlines.Count != 0) {
                    summary.Inlines.Add(new System.Windows.Documents.Run(" · "));
                }
                var run = new System.Windows.Documents.Run($"{label}：{value}");
                if (invalid) {
                    run.Foreground = (WpfBrush)FindResource("Brush.Error.Foreground");
                }
                summary.Inlines.Add(run);
            }
        }
    }

    // Saving a value or keeping a draft refreshes folded summaries and the open inspector's notes in place, so the
    // focused editor and a switch's animation are not rebuilt.
    private void RefreshParameterDisplays()
    {
        foreach (var (summary, task) in _planItemSummaries) {
            FillParameterSummary(summary, task.Name, GetConfigurationOrEmpty(task.Name, task.Kind), task.Kind);
        }
        if (_expandedTaskName is not { } expanded || _inspectorRows.Count == 0) {
            return;
        }
        foreach (var row in DeriveInspectorRows(expanded, GetConfigurationOrEmpty(expanded, _expandedTaskKind),
            shared: false, _expandedTaskKind)) {
            if (_inspectorRows.TryGetValue(row.Option.Name, out var view)) {
                ApplyInspectorRow(view, row);
            }
        }
    }

    private Border CreateParameterInspector(string taskName, ProjectConfigurationView configuration, PlanItemKind kind)
    {
        var table = new StackPanel();
        foreach (var row in DeriveInspectorRows(taskName, configuration, shared: true, kind)) {
            table.Children.Add(CreateInspectorRow(row, separated: table.Children.Count != 0));
        }
        return new Border { Style = (Style)FindResource("Inspector.Table"), Child = table };
    }

    private ParameterRowPanel CreateInspectorRow(InspectorRow row, bool separated)
    {
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = row.Label, Style = (Style)FindResource("Inspector.Label") });
        if (!string.IsNullOrWhiteSpace(row.Description)) {
            label.Children.Add(new TextBlock {
                Text = row.Description, Style = (Style)FindResource("Inspector.Description")
            });
        }
        if (row.IsGlobal || row.SharedWith.Count != 0) {
            label.Children.Add(CreateSharedHint(row));
        }
        var caseNotes = new StackPanel();
        var inputNotes = new Dictionary<string, StackPanel>(StringComparer.Ordinal);
        var value = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (row.Kind == ProjectOptionKind.Input) {
            value.Children.Add(CreateInputCells(row, inputNotes));
        } else {
            value.Children.Add(CreateCaseSelector(row));
            value.Children.Add(caseNotes);
        }
        var panel = new ParameterRowPanel {
            Depth = row.Depth, Separated = separated, Style = (Style)FindResource("Inspector.Row"),
            LineBrush = (WpfBrush)FindResource("Brush.Border.Subtle"),
            GuideBrush = (WpfBrush)FindResource("Brush.Parameter.Guide"),
            InvalidBrush = (WpfBrush)FindResource("Brush.Error"),
            Children = {
                new Border {
                    Padding = new Thickness(12 + row.Depth * ParameterRowPanel.IndentStep, 8, 12, 8), Child = label
                },
                new Border { Padding = new Thickness(10, 6, 10, 6), Child = value }
            }
        };
        if (row.Depth > 0) {
            panel.Background = (WpfBrush)FindResource("Brush.Parameter.Nested");
        }
        var view = new InspectorRowView(panel, caseNotes, inputNotes);
        _inspectorRows[row.Option.Name] = view;
        ApplyInspectorRow(view, row);
        return panel;
    }

    private StackPanel CreateSharedHint(InspectorRow row)
    {
        var icon = CreateSymbolIcon(WpfSymbolRegular.Link16);
        icon.Width = icon.Height = icon.FontSize = 12;
        icon.Margin = new Thickness(0, 0, 4, 0);
        icon.Foreground = (WpfBrush)FindResource("Brush.Text.Muted");
        var text = row.IsGlobal ? "所有任务共用" : $"与 {row.SharedWith.Count} 个任务共用";
        var hint = new StackPanel {
            Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Background = WpfBrushes.Transparent,
            ToolTip = row.IsGlobal ? "全局参数：执行计划中的所有任务共用一个值"
                : $"同一配置中共用一个值：\n{string.Join("、", row.SharedWith)}",
            Children = { icon, new TextBlock { Text = text, Style = (Style)FindResource("Inspector.Shared") } }
        };
        AutomationProperties.SetName(hint, row.IsGlobal ? text : $"{text}：{string.Join("、", row.SharedWith)}");
        return hint;
    }

    private FrameworkElement CreateInputCells(InspectorRow row, Dictionary<string, StackPanel> notes)
    {
        if (row.Inputs.Count == 1) {
            var input = row.Inputs[0];
            notes[input.Input.Name] = new StackPanel();
            return new StackPanel { Children = { LimitWidth(CreateInputEditor(row, input)), notes[input.Input.Name] } };
        }
        var panel = new ParameterInputsPanel();
        foreach (var input in row.Inputs) {
            var header = new StackPanel { MinHeight = 20 };
            header.Children.Add(new TextBlock {
                Text = input.Label, Style = (Style)FindResource("Inspector.InputLabel")
            });
            if (!string.IsNullOrWhiteSpace(input.HelpText)) {
                header.Children.Add(new TextBlock {
                    Text = input.HelpText, Style = (Style)FindResource("Inspector.Description")
                });
            }
            notes[input.Input.Name] = new StackPanel();
            panel.Children.Add(new StackPanel {
                Children = { header, CreateInputEditor(row, input), notes[input.Input.Name] }
            });
        }
        return panel;
    }

    private Wpf.Ui.Controls.TextBox CreateInputEditor(InspectorRow row, InspectorInput input)
    {
        var tag = new OptionInputTag(_projectPlan!.ActiveConfigurationId, row.Option.Name, input.Input.Name,
            input.SavedValue, input.Input.PatternMessage);
        var editor = new Wpf.Ui.Controls.TextBox {
            Style = (Style)FindResource("Option.TextBox"),
            Text = input.DisplayValue,
            Tag = tag,
            ToolTip = input.Input.PatternMessage
        };
        AutomationProperties.SetName(editor, input.AccessibleName);
        if (!string.IsNullOrWhiteSpace(input.HelpText)) {
            AutomationProperties.SetHelpText(editor, input.HelpText);
        }
        editor.LostKeyboardFocus += OptionInputTextBox_LostKeyboardFocus;
        editor.PreviewKeyDown += OptionInputTextBox_PreviewKeyDown;
        BindConfigurationEditable(editor);
        _optionInputEditors[tag.Key] = editor;
        if (input.Draft is { } draft) {
            ShowOptionInputError(editor, draft.Message);
        }
        return editor;
    }

    private FrameworkElement CreateCaseSelector(InspectorRow row)
    {
        var option = row.Option;
        var configurationId = _projectPlan!.ActiveConfigurationId;
        System.Windows.Controls.Control selector;
        FrameworkElement field;
        if (option.Kind == ProjectOptionKind.Switch) {
            // One click flips a switch; the on/off captions keep the PI case labels visible.
            var (on, off) = GetSwitchCases(option);
            var toggle = new Wpf.Ui.Controls.ToggleSwitch {
                IsChecked = option.SelectedCase == on.Name, OnContent = on.Label, OffContent = off.Label,
                Style = (Style)FindResource("Option.ToggleSwitch"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggle.Click += (_, _) =>
                SaveSelectedCase(configurationId, option.Name, toggle.IsChecked == true ? on.Name : off.Name);
            selector = toggle;
            field = new Grid { Height = (double)FindResource("Height.OptionEditor"), Children = { toggle } };
        } else {
            var comboBox = new WpfComboBox {
                ItemsSource = option.Cases,
                DisplayMemberPath = nameof(ProjectCaseEditor.Label),
                SelectedItem = row.SelectedCase,
                Style = (Style)FindResource("Option.ComboBox")
            };
            comboBox.SelectionChanged += (_, _) => {
                if (comboBox.SelectedItem is ProjectCaseEditor selected) {
                    SaveSelectedCase(configurationId, option.Name, selected.Name);
                }
            };
            selector = comboBox;
            field = LimitWidth(comboBox);
        }
        AutomationProperties.SetName(selector, row.Label);
        if (!string.IsNullOrWhiteSpace(row.Description)) {
            AutomationProperties.SetHelpText(selector, row.Description);
        }
        BindConfigurationEditable(selector);
        return field;
    }

    // A single editor keeps a readable width on a wide card and still shrinks with a narrow one.
    private static Grid LimitWidth(UIElement editor) =>
        new() { ColumnDefinitions = { new ColumnDefinition { MaxWidth = 340 } }, Children = { editor } };

    private void ApplyInspectorRow(InspectorRowView view, InspectorRow row)
    {
        view.Row.IsInvalid = row.IsInvalid;
        view.CaseNotes.Children.Clear();
        if (row.CaseDescription is { } caseDescription) {
            view.CaseNotes.Children.Add(CreateInspectorNote(caseDescription, "Inspector.Note"));
        }
        foreach (var input in row.Inputs) {
            if (!view.InputNotes.TryGetValue(input.Input.Name, out var notes)) {
                continue;
            }
            notes.Children.Clear();
            if (input.Draft is { } draft) {
                var saved = ParameterInspector.ShowValue(input.SavedValue);
                notes.Children.Add(CreateInspectorNote(draft.Message, "Inspector.Error"));
                notes.Children.Add(CreateInspectorNote($"未保存，仍使用「{saved}」· Esc 还原", "Inspector.DraftNote"));
            }
        }
    }

    private TextBlock CreateInspectorNote(string text, string style)
    {
        var note = new TextBlock { Text = text, Style = (Style)FindResource(style) };
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        return note;
    }

    // PI switch cases carry no on/off flag; MaaNOP names them Yes/No, so an affirmative name marks "on",
    // otherwise the first case does.
    private static (ProjectCaseEditor On, ProjectCaseEditor Off) GetSwitchCases(ProjectOptionEditor option)
    {
        var on = option.Cases.FirstOrDefault(
            item => SwitchOnCaseNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) ?? option.Cases[0];
        return (on, option.Cases.First(item => item.Name != on.Name));
    }

    private void BindConfigurationEditable(UIElement element) => System.Windows.Data.BindingOperations.SetBinding(
        element, IsEnabledProperty, _configurationEditableBinding ??= new System.Windows.Data.Binding {
            Path = new PropertyPath(ConfigurationEditableProperty), Source = this
        });

    private WpfButton CreateIconButton(WpfSymbolRegular symbol, string accessibleName, string toolTip)
    {
        var button = new WpfButton {
            Content = CreateSymbolIcon(symbol),
            Style = (Style)FindResource("TaskIconButtonStyle"),
            ToolTip = toolTip
        };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static WpfSymbolIcon CreateSymbolIcon(WpfSymbolRegular symbol) => new() {
        Symbol = symbol,
        Width = 16,
        Height = 16,
        Focusable = false,
        IsHitTestVisible = false,
        ToolTip = null
    };

    private void AddDropIndicator()
    {
        var indicator = new Border {
            Height = 2,
            Margin = new Thickness(12, 0, 12, 0),
            Background = WpfBrushes.Transparent,
            IsHitTestVisible = false
        };
        _dropIndicators.Add(indicator);
        PlanItemsPanel.Children.Add(indicator);
    }

    private void PlanDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfButton { Tag: ProjectTaskChoice task }) {
            _dragTaskName = task.Name;
            _dragStartPoint = e.GetPosition(PlanItemsPanel);
        }
    }

    private void PlanDragHandle_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not WpfButton { Tag: ProjectTaskChoice task }
            || _dragTaskName != task.Name) {
            return;
        }
        var current = e.GetPosition(PlanItemsPanel);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) {
            return;
        }

        _expandedTaskName = null;
        RenderTaskPlan();
        if (_planItemContainers.TryGetValue((task.Kind, task.Name), out var container)) {
            container.Effect = (Effect)FindResource("Effect.Surface.Drag");
        }
        try {
            var data = new WpfDataObject(PlanItemDragDataFormat, task.Name);
            _ = DragDrop.DoDragDrop(PlanItemsPanel, data, WpfDragDropEffects.Move);
        } finally {
            _dragTaskName = null;
            ClearDropIndicators();
            RenderTaskPlan();
        }
    }

    private void PlanItemsPanel_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!e.Data.GetDataPresent(PlanItemDragDataFormat)) {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }
        var boundary = CalculateDropBoundary(e.GetPosition(PlanItemsPanel).Y);
        ShowDropIndicator(boundary);
        e.Effects = WpfDragDropEffects.Move;
        e.Handled = true;
    }

    private void PlanItemsPanel_DragLeave(object sender, WpfDragEventArgs e) => ClearDropIndicators();

    private void PlanItemsPanel_Drop(object sender, WpfDragEventArgs e)
    {
        if (!CurrentControls.ConfigurationEditable || e.Data.GetData(PlanItemDragDataFormat) is not string taskName) {
            return;
        }
        try {
            var project = RequireProject();
            var currentIndex = project.SelectedTaskNames.ToList().IndexOf(taskName);
            var boundary = CalculateDropBoundary(e.GetPosition(PlanItemsPanel).Y);
            var targetIndex = boundary > currentIndex ? boundary - 1 : boundary;
            targetIndex = Math.Clamp(targetIndex, 0, project.SelectedTaskNames.Count - 1);
            _expandedTaskName = null;
            if (project.MoveTask(taskName, targetIndex)) {
                _pendingStartAttempt = null;
                _logger.Info($"已调整执行计划顺序：{taskName} -> {targetIndex}。 ");
            }
            RenderTaskPlan();
            e.Effects = WpfDragDropEffects.Move;
            e.Handled = true;
        } catch (Exception exception) {
            HandleProjectEditError("调整执行计划顺序失败", exception);
        }
    }

    private int CalculateDropBoundary(double pointerY)
    {
        var project = _projectPlan;
        if (project is null) {
            return 0;
        }
        for (var index = 0; index < project.SelectedTaskNames.Count; index++) {
            var container = _planItemContainers[(PlanItemKind.MaaTask, project.SelectedTaskNames[index])];
            var top = container.TranslatePoint(new WpfPoint(0, 0), PlanItemsPanel).Y;
            if (pointerY < top + container.ActualHeight / 2) {
                return index;
            }
        }
        return project.SelectedTaskNames.Count;
    }

    private void ShowDropIndicator(int boundary)
    {
        ClearDropIndicators();
        if (boundary >= 0 && boundary < _dropIndicators.Count) {
            _dropIndicators[boundary].Background = (WpfBrush)FindResource("Brush.TasksAccent");
        }
    }

    private void ClearDropIndicators()
    {
        foreach (var indicator in _dropIndicators) {
            indicator.Background = WpfBrushes.Transparent;
        }
    }

    private void HandleProjectEditError(string operation, Exception exception)
    {
        HandleOperationError(operation, exception);
        TryRenderTaskPlan();
    }

    private void ShowProjectValidationError(Exception exception)
    {
        ProjectValidationText.Text = exception.GetBaseException().Message;
        ProjectValidationBorder.Visibility = Visibility.Visible;
    }

    private async Task RunOperationAsync(PendingOperation pending, Func<Task> operation)
    {
        if (OperationOrExitInProgress) {
            return;
        }

        SetPendingOperation(pending);
        try {
            await _environment.RunExclusiveAsync(operation);
        } catch (OperationCanceledException) {
            _logger.Warn($"操作已取消：正在{OperationLabel(pending)}...");
        } catch (Exception exception) {
            HandleOperationError($"{OperationLabel(pending)}失败", exception);
        } finally {
            SetPendingOperation(PendingOperation.None);
        }
    }

    // One label per operation feeds its progress text, cancellation log and failure dialog title.
    private static string OperationLabel(PendingOperation operation) => operation switch {
        PendingOperation.RestoringSession => "恢复已有桌面分身",
        PendingOperation.ShowingDesktop => "显示子桌面",
        PendingOperation.PreparingEnvironment => "准备运行环境",
        PendingOperation.StartingRun => "开始任务",
        PendingOperation.StoppingRun => "停止任务",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private void HandleOperationError(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        ShowActionableError(operation, exception, GetRecoveryGuidance(operation), offerLogDirectory: true);
    }

    private static string GetRecoveryGuidance(string operation)
    {
        if (operation.Contains("桌面分身", StringComparison.Ordinal)
            || operation.Contains("子桌面", StringComparison.Ordinal)) {
            return "请确认程序以管理员权限运行，并检查桌面分身状态后重试。";
        }

        return "请检查当前配置和系统状态后重试。";
    }

    private void LoadProject()
    {
        var configPath = Path.Combine(_applicationDirectory, "config", "maanop-config.json");
        if (_onboardingPreferences is null) {
            _onboardingPreferences = new OnboardingPreferences(Path.GetDirectoryName(configPath)!, _logger);
            _onboardingPreferences.CaptureBeforeProjectLoad(configPath);
        }
        var project = ProjectPlanModule.Open(_applicationDirectory, configPath);
        _projectPlan = project;
        // Preparing the environment reloads the project; an unfixed invalid draft must survive it and keep blocking
        // Start. Drafts for inputs the reloaded PI no longer has are never rendered or matched.
        _pendingStartAttempt = null;
        _expandedTaskKind = PlanItemKind.MaaTask;
        _expandedTaskName = project.InitializedTaskName
            ?? (_onboardingPreferences.ShouldOfferAutomatically && !_onboardingAutoEnded
                ? project.SelectedTaskNames.FirstOrDefault() : null);
        CloseTaskDescriptionDrawer();
        ProjectEmptyStatePanel.Visibility = Visibility.Collapsed;
        TaskWorkspacePanel.Visibility = Visibility.Visible;

        RenderTaskPlan();
        if (project.LoadWarning is not null) {
            _logger.Warn(project.LoadWarning);
        }
        _logger.Info(
            $"已加载 MaaNOP Project Interface：{project.ProjectName} {project.ProjectVersion}；"
            + $"interfaceDigest={project.SourceInterfaceDigest}；"
            + $"runtimeProfileDigest={project.RuntimeProfileDigest}。 ");
    }

    private ProjectPlanModule RequireProject() =>
        _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。");

    private void ShowProjectUnavailableState(
        string emptyStateTitle,
        string emptyStateDetail)
    {
        _projectPlan = null;
        _invalidInputDrafts.Clear();
        _pendingStartAttempt = null;
        _projectConfigurationValid = false;
        _expandedTaskName = null;
        AvailableTasksPanel.Children.Clear();
        PlanItemsPanel.Children.Clear();
        CloseTaskDescriptionDrawer();
        ProjectEmptyStateTitleText.Text = emptyStateTitle;
        ProjectEmptyStateDetailText.Text = emptyStateDetail;
        TaskWorkspacePanel.Visibility = Visibility.Collapsed;
        ProjectEmptyStatePanel.Visibility = Visibility.Visible;
    }

    internal static bool IsUserFacingRunLog(WorkerLogEntry entry) =>
        string.Equals(entry.Source, ProtocolConstants.MaaNopRunLogSource, StringComparison.Ordinal);

    // The task label comes from the Run Plan the Worker reports, falling back to the name the log carries.
    internal static LogEntry? CreateUserFacingRunLogEntry(WorkerLogEntry workerEntry, WorkerSnapshot? snapshot = null)
    {
        if (!IsUserFacingRunLog(workerEntry)) {
            return null;
        }

        var timestampUtc = DateTime.SpecifyKind(workerEntry.TimestampUtc, DateTimeKind.Utc);
        string? label = null;
        if (workerEntry.PlanItemId is { } planItemId) {
            label = new[] { snapshot?.ActiveRun, snapshot?.LastRun }.OfType<RunSnapshot>()
                .SelectMany(run => run.Plan.Items).FirstOrDefault(item => item.PlanItemId == planItemId)?.TaskLabel;
            label = string.IsNullOrWhiteSpace(label) ? workerEntry.TaskName : label;
        }
        return new LogEntry(
            new DateTimeOffset(timestampUtc).ToLocalTime(),
            ParseWorkerLogLevel(workerEntry.Level),
            workerEntry.Message, workerEntry.RunId, workerEntry.PlanItemId,
            string.IsNullOrWhiteSpace(label) ? null : label);
    }

    internal static void WriteWorkerDiagnosticLog(AppLogger logger, WorkerLogEntry entry) =>
        logger.Write(ParseWorkerLogLevel(entry.Level), $"Worker #{entry.Sequence} [{entry.Source}] {entry.Message}");

    private void AddRunLogEntry(WorkerLogEntry workerEntry)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => AddRunLogEntry(workerEntry));
            return;
        }

        var entry = CreateUserFacingRunLogEntry(workerEntry, _workerSnapshot.WorkerSnapshot);
        if (entry is null) {
            return;
        }
        var shouldFollow = _followLogs && IsLogNearTop();
        var previousOffset = _homeLogScrollViewer?.VerticalOffset ?? 0;
        var inserted = _runLog.Add(entry);
        UpdateRunLogCount();
        if (!shouldFollow && _runLog.EntryCount > 1) {
            _homeLogScrollViewer?.ScrollToVerticalOffset(previousOffset + inserted);
        }

        if (shouldFollow) {
            _ = Dispatcher.BeginInvoke(ScrollLogsToLatest, DispatcherPriority.Background);
            return;
        }

        _followLogs = false;
    }

    private void OnWorkerStateChanged(object? sender, WorkerCoordinatorSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnWorkerStateChanged(sender, snapshot));
            return;
        }

        // A change raised on another thread runs here late, possibly after a newer one raised on this thread, so the
        // coordinator's current snapshot is applied rather than the one the event carried.
        _workerSnapshot = _workerCoordinator.Snapshot;
        RefreshRuntimeControls();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ReevaluateOnboarding();

    private void MainWindow_StateChanged(object? sender, EventArgs e) => ReevaluateOnboarding();

    private bool IsGlobalModalOpen => UpdateOverlay.Visibility == Visibility.Visible;

    private void EnsureModalWindowHook()
    {
        if (_modalWindowSource is null && PresentationSource.FromVisual(MainWindowContent) is HwndSource source) {
            _modalWindowSource = source;
            source.AddHook(ModalWindowHook);
        }
    }

    private void RemoveModalWindowHook()
    {
        _modalWindowSource?.RemoveHook(ModalWindowHook);
        _modalWindowSource = null;
    }

    private nint ModalWindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!IsGlobalModalOpen) {
            return 0;
        }
        // WPF-UI caption buttons handle native messages independently of WPF overlay hit testing.
        const int wmNcHitTest = 0x0084;
        const int wmNcLeftButtonDown = 0x00A1;
        const int wmNcLeftButtonUp = 0x00A2;
        const int wmNcLeftButtonDoubleClick = 0x00A3;
        const int wmSysCommand = 0x0112;
        if (message == wmNcHitTest) {
            handled = true;
            return 1; // HTCLIENT routes caption-area clicks to the dismiss scrim.
        }
        var command = (int)(wParam.ToInt64() & 0xFFF0);
        if (message is wmNcLeftButtonDown or wmNcLeftButtonUp or wmNcLeftButtonDoubleClick
            || message == wmSysCommand && command is 0xF020 or 0xF030 or 0xF060 or 0xF120) {
            handled = true;
        }
        return 0;
    }

    private void OnWorkerLogReceived(object? sender, WorkerLogEntry entry)
    {
        WriteWorkerDiagnosticLog(_logger, entry);

        if (IsUserFacingRunLog(entry)) {
            AddRunLogEntry(entry);
        }
    }

    private static LogLevel ParseWorkerLogLevel(string level) => level.ToLowerInvariant() switch {
        "critical" => LogLevel.Critical,
        "error" => LogLevel.Error,
        "warning" or "warn" => LogLevel.Warn,
        "debug" => LogLevel.Debug,
        _ => LogLevel.Info
    };

    private void LogListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scrollViewer) {
            _homeLogScrollViewer = scrollViewer;
        }

        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0) {
            _followLogs = IsLogNearTop();
        }
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        _runLog.Clear();
        UpdateRunLogCount();
        _followLogs = true;
    }

    private void UpdateRunLogCount() =>
        RunLogCountText.Text = _runLog.EntryCount == 0 ? string.Empty : $"{_runLog.EntryCount} 条";

    private bool IsLogNearTop()
    {
        _homeLogScrollViewer ??= FindVisualChild<ScrollViewer>(HomeLogListBox);
        return _homeLogScrollViewer is null || _homeLogScrollViewer.VerticalOffset <= 0.1;
    }

    private void ScrollLogsToLatest()
    {
        if (_followLogs && LogLines.FirstOrDefault() is { } latestHeading) {
            HomeLogListBox.ScrollIntoView(latestHeading);
        }
    }

    private void OnSessionStateChanged(object? sender, ChildSessionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, snapshot));
            return;
        }

        _sessionSnapshot = snapshot;
        RefreshRuntimeControls();
    }

    // Raised by whichever entry ends the Runtime Environment, so the tray's end shows here as well.
    private void OnEnvironmentStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnEnvironmentStateChanged(sender, e));
            return;
        }

        RefreshRuntimeControls();
    }

    // Another operation is running or the application is exiting, so a new operation must not start.
    private bool OperationOrExitInProgress => _pendingOperation != PendingOperation.None || _exitInProgress;

    private void SetPendingOperation(PendingOperation operation)
    {
        _pendingOperation = operation;
        Mouse.OverrideCursor = operation != PendingOperation.None ? System.Windows.Input.Cursors.Wait : null;
        RefreshRuntimeControls();
    }

    // Derived fresh on every read, so a click handler never acts on a decision the window no longer shows.
    private RuntimeControlState CurrentControls => RuntimeControls.Derive(new RuntimeControlInputs(
        _sessionSnapshot, _workerSnapshot, _projectPlan is { } project
            ? new ProjectReadiness(true, project.SelectedItemCount, _projectConfigurationValid,
                project.RuntimeProfileDigest)
            : ProjectReadiness.NotLoaded,
        _pendingOperation, _environmentPreparationFailed, _exitInProgress, _environment.Ending,
        _environment.EndUnconfirmed));

    // The only refresh path for runtime inputs.
    private void RefreshRuntimeControls()
    {
        ReevaluateOnboarding();
        ApplyRuntimeControls(CurrentControls);
    }

    internal void ApplyRuntimeControls(RuntimeControlState state)
    {
        ApplyDesktopToggle(state.Desktop);

        var action = state.Action;
        PrepareEnvironmentButton.Visibility = Visible(action is RuntimeAction.Prepare);
        PrepareEnvironmentButton.IsEnabled = action is RuntimeAction.Prepare { Enabled: true };
        RetryEnvironmentButton.Visibility = Visible(action is RuntimeAction.Retry);
        RetryEnvironmentButton.IsEnabled = action is RuntimeAction.Retry { Enabled: true };
        StartTaskHeaderButton.Visibility = Visible(action is RuntimeAction.Start);
        StartTaskHeaderButton.IsEnabled = action is RuntimeAction.Start { Enabled: true };
        Explain(StartTaskHeaderButton, (action as RuntimeAction.Start)?.Blocker);
        Explain(RetryEnvironmentButton, (action as RuntimeAction.Retry)?.Blocker);
        if (action is RuntimeAction.Retry { Target: var retryTarget }) {
            var retryText = retryTarget == RetryTarget.Prepare ? "重试准备运行环境" : "重试开始任务";
            RetryEnvironmentText.Text = retryText;
            AutomationProperties.SetName(RetryEnvironmentButton, retryText);
        }
        StopTaskHeaderButton.Visibility = Visible(action is RuntimeAction.Stop);
        StopTaskHeaderButton.IsEnabled = action is RuntimeAction.Stop { Enabled: true };
        RecoverEnvironmentButton.Visibility = Visible(action is RuntimeAction.Recover);
        RecoverEnvironmentButton.IsEnabled = action is RuntimeAction.Recover { Enabled: true };
        if (action is RuntimeAction.Recover { Reason: var recovery }) {
            var reason = RecoveryText(recovery);
            RecoverEnvironmentButton.ToolTip = reason;
            AutomationProperties.SetHelpText(RecoverEnvironmentButton, reason);
        }
        RuntimeHeaderProgressRing.Visibility = Visible(action is RuntimeAction.InProgress);
        if (action is RuntimeAction.InProgress { Kind: var kind }) {
            var operation = kind switch {
                ProgressKind.StoppingRun => PendingOperation.StoppingRun,
                ProgressKind.StartingRun => PendingOperation.StartingRun,
                _ => PendingOperation.PreparingEnvironment
            };
            var progressText = kind == ProgressKind.EndingEnvironment
                ? "正在结束桌面分身" : $"正在{OperationLabel(operation)}";
            AutomationProperties.SetName(RuntimeHeaderProgressRing, progressText);
            RuntimeProgressText.Text = $"{progressText}…";
        }
        ApplyMoreMenu(state);
        var progress = CurrentRunProgress;
        ApplyRuntimeStatus(state, progress);
        ApplyRunProgress(progress);
        ApplyPanelSummary(state, progress);

        SetValue(ConfigurationEditableProperty, state.ConfigurationEditable);
        if (!state.ConfigurationEditable && _taskShelfExpanded) {
            SetTaskShelfExpanded(false);
        }
        ConfigurationLockBadge.Visibility = Visible(state.ConfigurationLock is not null);
        _configurationLockText = state.ConfigurationLock == LockReason.RunActive
            ? "任务运行中，配置已锁定" : "运行环境处理中，配置暂时锁定";
        UpdateConfigurationLockText();
        RefreshConfigurationTabStates();

        static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        // A blocked Start, or a Retry that would start, says why on hover and to screen readers; the task area
        // holds the detailed message. The visible label needs no tooltip of its own.
        static void Explain(FrameworkElement button, StartBlocker? blocker)
        {
            var reason = blocker is { } value ? StartBlockerText(value) : null;
            button.ToolTip = reason;
            AutomationProperties.SetHelpText(button, reason ?? string.Empty);
        }
    }

    private void ApplyPanelSummary(RuntimeControlState state, RunProgressView? progress)
    {
        var running = state.Desktop.Status is DesktopStatus.Background or DesktopStatus.Viewing
            or DesktopStatus.Interactive;
        RuntimeStatusText.Text = running ? "已启动" : state.Desktop.Status switch {
            DesktopStatus.Connecting => "启动中",
            DesktopStatus.Ending => "正在结束",
            DesktopStatus.Disconnected => "已断开",
            _ => "未启动"
        };
        RuntimeStatusPill.Background = System.Windows.Media.Brushes.Transparent;
        RuntimeStatusText.Foreground = (WpfBrush)FindResource("Brush.Text.Secondary");
        SessionStatusDot.Fill = running ? (WpfBrush)FindResource("Brush.Success")
            : System.Windows.Media.Brushes.Transparent;
        SessionStatusDot.Stroke = running ? null : (WpfBrush)FindResource("Brush.Border.Strong");
        SessionStatusDot.StrokeThickness = running ? 0 : 1.5;
        AutomationProperties.SetName(RuntimeStatusPill, $"桌面分身：{RuntimeStatusText.Text}");
        DesktopViewText.Text = state.Desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口";
        DesktopViewButton.ToolTip = state.Desktop.Action == DesktopAction.Hide
            ? "隐藏窗口，分身和任务继续运行" : "显示分身窗口，默认只读";
        StartSessionMenuItem.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StartSessionMenuItem.IsEnabled = state.Action is RuntimeAction.Prepare { Enabled: true };
        RuntimeStatusPill.ToolTip = state.Desktop.Status == DesktopStatus.Connecting ? PreparationStage() : null;
        EndEnvironmentMenuItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        SessionMenuSeparator.Visibility = StartSessionMenuItem.Visibility == Visibility.Visible || running
            ? Visibility.Visible : Visibility.Collapsed;
        if (state.Action is RuntimeAction.InProgress { Kind: var busyKind }) {
            RuntimeProgressText.Text = busyKind == ProgressKind.StoppingRun ? "正在停止…"
                : busyKind == ProgressKind.EndingEnvironment ? "正在结束…" : "启动中…";
        }
        RunSummaryCard.Visibility = progress is not null || state.Action is RuntimeAction.Recover
            ? Visibility.Visible : Visibility.Collapsed;
        if (progress is null || state.Action is RuntimeAction.Recover) {
            return;
        }
        RunSummaryCard.Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                progress.Failed > 0 ? "#FEF4F5" : "#F6F8FB"));
        RunSummaryCard.BorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                progress.Failed > 0 ? "#F2D0D4" : "#E4EAF0"));
        RuntimeStatusDetail.Visibility = Visibility.Collapsed;
        RuntimeStatusTitle.Text = progress.Current?.TaskLabel
            ?? progress.Items.FirstOrDefault(item => item.State == PlanItemProgress.Failed)?.TaskLabel
            ?? (progress.Stopped > 0 ? "任务已停止" : $"{progress.Succeeded} 项全部成功");
        RunProgressCaption.Text = progress.Active
            ? $"{(state.Action is RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } ? "停止中" : "运行中")}"
                + $" · 第 {(progress.CurrentIndex ?? 0) + 1} / {progress.Total} 项"
            : progress.Failed == 0 && progress.Stopped == 0 && progress.NotRun == 0
                ? $"{progress.EndedAtUtc?.ToLocalTime():HH:mm} 完成"
                : $"{(progress.Failed > 0 ? "失败" : "已停止")} · "
                    + RunProgress.Caption(progress).Replace("上次运行：", "");
        RunProgressCaption.Foreground = (WpfBrush)FindResource(progress.Active ? "Brush.Primary"
            : progress.Failed > 0 ? "Brush.Error.Foreground" : "Brush.Success");
        RunProgressCaption.FontWeight = FontWeights.SemiBold;
    }

    private static string StartBlockerText(StartBlocker blocker) => blocker switch {
        StartBlocker.CommandsUnavailable => "当前操作完成后可开始任务",
        StartBlocker.ProjectNotLoaded => "MaaNOP 项目未加载，无法开始任务",
        StartBlocker.NoTasks => "请先在执行计划中添加任务",
        StartBlocker.ConfigurationInvalid => "当前配置未通过校验，请先修正任务参数",
        _ => "运行环境尚未就绪，请稍候"
    };

    private static string RecoveryText(RecoveryReason reason) => reason switch {
        RecoveryReason.StopUnconfirmed => "任务未能确认结束，MaaNOP 可能仍在运行",
        RecoveryReason.RuntimeFaulted => "运行环境故障，Worker 无法继续执行任务",
        RecoveryReason.WorkerLost => "Worker 失联且未确认已退出；也可以等待它重新连接",
        _ => "上次结束桌面分身未确认"
    };

    // The glyph and dot report the Child Session and the fill marks an open viewer; it deliberately has no tooltip.
    // The dot takes the status pill's tone colours, so both agree on what amber, green, blue and red mean.
    private void ApplyDesktopToggle(DesktopToggle desktop)
    {
        DesktopViewIcon.Data = Geometry.Parse(desktop.Action == DesktopAction.Hide
            ? "M2.6 10s2.7-5 7.4-5c1.5 0 2.8.5 3.9 1.2M17.4 10s-2.7 5-7.4 5c-1.5 0-2.8-.5-3.9-1.2M3.5 16.5l13-13"
            : "M2.6 10s2.7-5 7.4-5 7.4 5 7.4 5-2.7 5-7.4 5-7.4-5-7.4-5Z"
                + "M12.3 10A2.3 2.3 0 1 1 7.7 10A2.3 2.3 0 1 1 12.3 10");
        DesktopViewButton.IsEnabled = desktop.Enabled;
        if (desktop.Status == DesktopStatus.Interactive) {
            DesktopViewButton.Background = (WpfBrush)FindResource("Brush.Warning.Surface");
            DesktopViewButton.Foreground = (WpfBrush)FindResource("Brush.Warning.Foreground");
        } else {
            DesktopViewButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            DesktopViewButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
        AutomationProperties.SetName(DesktopViewButton,
            desktop.Action == DesktopAction.Hide ? "隐藏窗口" : "显示窗口");
    }

    private void ApplyMoreMenu(RuntimeControlState state)
    {
        var endBlocker = state.Action is RuntimeAction.Stop ? "请先停止任务" : EndEnvironmentBlocker(state);
        EndEnvironmentMenuItem.IsEnabled = endBlocker is null;
        EndEnvironmentMenuItem.InputGestureText = endBlocker ?? string.Empty;
        EndEnvironmentMenuItem.Tag = endBlocker ?? "注销分身，游戏随之关闭";
    }

    private RunProgressView? CurrentRunProgress => _projectPlan is { } project
        ? RunProgress.Derive(_workerSnapshot.WorkerSnapshot,
            project.SelectedItems.Select(item => (item.Kind, item.Name)).ToArray()) : null;

    private enum StatusTone { Idle, Busy, Ok, Run, Bad }

    private static (string Surface, string Foreground) ToneBrushes(StatusTone tone) => tone switch {
        StatusTone.Busy => ("Brush.Warning.Surface", "Brush.Warning.Foreground"),
        StatusTone.Ok => ("Brush.TasksAccent.Surface", "Brush.Success"),
        StatusTone.Run => ("Brush.Primary.Surface", "Brush.Primary"),
        StatusTone.Bad => ("Brush.Error.Surface", "Brush.Error"),
        _ => ("Brush.Surface.Disabled", "Brush.Text.Secondary")
    };

    // The pill, title and detail restate the action RuntimeControls chose; progress only adds the current item.
    private void ApplyRuntimeStatus(RuntimeControlState state, RunProgressView? progress)
    {
        var planSummary = _projectPlan is { } project ? $"{project.SelectedItemCount} 项任务待执行" : "项目未加载";
        var current = progress?.Current;
        var (tone, pill, title, detail, warn) = state.Action switch {
            RuntimeAction.Prepare => (StatusTone.Idle, "未准备", "开始任务前需要准备运行环境",
                "将准备桌面分身和 Worker", false),
            RuntimeAction.InProgress { Kind: ProgressKind.PreparingEnvironment } => (StatusTone.Busy, "准备中",
                "正在准备运行环境", PreparationStage(), false),
            RuntimeAction.InProgress { Kind: ProgressKind.StartingRun } => (StatusTone.Busy, "启动中", "正在开始任务",
                "正在把执行计划交给 Worker…", false),
            RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun } => (StatusTone.Busy, "停止中", "正在停止任务",
                "等待当前任务安全结束…", false),
            RuntimeAction.InProgress => (StatusTone.Busy, "处理中", "正在结束桌面分身", "正在注销 Child Session…", false),
            RuntimeAction.Stop when current is not null => (StatusTone.Run, "运行中",
                $"第 {progress!.CurrentIndex + 1} / {progress.Total} 项：{current.TaskLabel}",
                $"本项已运行 {ElapsedText(current.StartedAtUtc, null)}", false),
            RuntimeAction.Stop => (StatusTone.Run, "运行中", "任务运行中", string.Empty, false),
            RuntimeAction.Start { Blocker: { } blocker } => (StatusTone.Ok, "就绪", planSummary,
                StartBlockerText(blocker), true),
            RuntimeAction.Start => (StatusTone.Ok, "就绪", planSummary,
                $"{ActiveConfigurationName} · 运行环境已就绪{(progress is null ? "" : "，可以再次开始")}", false),
            RuntimeAction.Retry { Target: RetryTarget.Prepare } => (StatusTone.Bad, "需重试", "运行环境未就绪",
                "重新准备运行环境后再开始任务", false),
            RuntimeAction.Retry retry => (StatusTone.Bad, "需重试", "上次运行失败",
                retry.Blocker is { } blocker ? StartBlockerText(blocker) : "修正后可以重新开始任务",
                retry.Blocker is not null),
            RuntimeAction.Recover { Reason: var reason } => (StatusTone.Bad, "故障", RecoveryTitle(reason),
                RecoveryText(reason), false),
            _ => (StatusTone.Idle, string.Empty, string.Empty, string.Empty, false)
        };
        var (surface, foreground) = ToneBrushes(tone);
        RuntimeStatusPill.Background = (WpfBrush)FindResource(surface);
        RuntimeStatusText.Foreground = (WpfBrush)FindResource(foreground);
        RuntimeStatusText.Text = pill;
        AutomationProperties.SetName(RuntimeStatusPill, $"运行状态：{pill}");
        RuntimeStatusTitle.Text = title;
        RuntimeStatusDetail.Text = detail;
        RuntimeStatusDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RuntimeStatusDetail.Foreground =
            (WpfBrush)FindResource(warn ? "Brush.Warning.Foreground" : "Brush.Text.Secondary");

        static string RecoveryTitle(RecoveryReason reason) => reason switch {
            RecoveryReason.StopUnconfirmed => "任务未能确认结束",
            RecoveryReason.RuntimeFaulted => "运行环境故障",
            RecoveryReason.WorkerLost => "Worker 失联",
            _ => "结束桌面分身未确认"
        };
    }

    // Preparation does not launch the game; describe only the stages Session and Worker observations confirm.
    private string PreparationStage() =>
        _sessionSnapshot.State is not (ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden)
            ? "正在连接桌面分身…"
            : _workerSnapshot.Observation == WorkerObservation.WorkerNotStarted ? "桌面分身已连接，等待 Worker 启动…"
            : _workerSnapshot.Observation == WorkerObservation.WorkerStarting ? "正在启动 Worker…"
            : _workerSnapshot is { Observation: WorkerObservation.Connected, SnapshotFresh: false }
                ? "正在同步 Worker 状态…" : "正在完成运行环境准备…";

    private string ActiveConfigurationName => _projectPlan is { } project
        ? project.Configurations.Single(item => item.Id == project.ActiveConfigurationId).Name : string.Empty;

    private static string ElapsedText(DateTime? startedAtUtc, DateTime? endedAtUtc) =>
        RunProgress.Elapsed(startedAtUtc, endedAtUtc, DateTime.UtcNow) is { } elapsed
            ? RunProgress.FormatDuration(elapsed) : "00:00";

    // RunProgress already hides a Last Run whose plan does not match the configuration on screen.
    private void ApplyRunProgress(RunProgressView? progress)
    {
        var shown = progress is not null;
        RunProgressPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (progress is not null) {
            RunProgressCaption.Text = RunProgress.Caption(progress);
            ApplyRunElapsed(progress);
            AutomationProperties.SetName(RunProgressBar, RunProgress.AccessibleCaption(progress));
        }
        _displayedProgress = shown ? progress : null;
        DrawRunProgressBar();
        ApplyPlanItemStatuses(progress);
        if (progress is { Active: true }) {
            _runTimer.Start();
        } else {
            _runTimer.Stop();
        }
    }

    private RunProgressView? _displayedProgress;

    private void RefreshRunTiming()
    {
        var progress = CurrentRunProgress;
        if (progress is not { Active: true }) {
            return;
        }
        ApplyRunElapsed(progress);
        if (CurrentControls.Action is RuntimeAction.Stop && progress.Current is { } current) {
            RuntimeStatusDetail.Text = $"本项已运行 {ElapsedText(current.StartedAtUtc, null)}";
        }
        if (_projectPlan is not { } project
            || !ProgressMatches(project, progress)) {
            return;
        }
        foreach (var view in _planItemStatuses.Values) {
            var item = progress.Items[view.Position];
            if (item.State == PlanItemProgress.Running) {
                view.Status.Text = $"运行中 · {ElapsedText(item.StartedAtUtc, null)}";
            }
        }
    }

    private void ApplyRunElapsed(RunProgressView progress) => RunProgressElapsed.Text = progress.Active
        ? $"总用时 {ElapsedText(progress.StartedAtUtc, null)}"
        : $"用时 {ElapsedText(progress.StartedAtUtc, progress.EndedAtUtc)}";

    private void RunProgressBar_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRunProgressBar();

    // Fill covers finished items only; the running item is marked by its position, never by an invented percentage.
    private void DrawRunProgressBar()
    {
        RunProgressBar.Children.Clear();
        if (_displayedProgress is not { Total: > 0 } progress || RunProgressBar.ActualWidth <= 0) {
            return;
        }
        var width = RunProgressBar.ActualWidth;
        var gap = progress.Total > 20 ? 0 : 2;
        var segment = (width - gap * (progress.Total - 1)) / progress.Total;
        void Add(double left, double size, string brush)
        {
            var bar = new System.Windows.Shapes.Rectangle {
                Width = Math.Max(0, size), Height = 6, Fill = (WpfBrush)FindResource(brush),
                RadiusX = gap == 0 ? 0 : 3, RadiusY = gap == 0 ? 0 : 3
            };
            Canvas.SetLeft(bar, left);
            RunProgressBar.Children.Add(bar);
        }
        for (var item = 0; item < progress.Total; item++) {
            var brush = progress.Items[item].State switch {
                PlanItemProgress.Succeeded => "Brush.Success",
                PlanItemProgress.Failed => "Brush.Error",
                PlanItemProgress.Running => "Brush.Primary",
                PlanItemProgress.Stopped => "Brush.Border.Strong",
                _ => "Brush.Progress.Track"
            };
            Add((segment + gap) * item, segment, brush);
        }
        RunProgressBar.Clip = new RectangleGeometry(new Rect(0, 0, width, 6), 3, 3);
    }

    private static bool ProgressMatches(ProjectPlanModule project, RunProgressView progress) =>
        progress.Items.Select(item => (item.Kind, item.TaskName))
            .SequenceEqual(project.SelectedItems.Select(item => (item.Kind, item.Name)));

    // Cards show their state only while the Run describes this plan; otherwise they show their number.
    private void ApplyPlanItemStatuses(RunProgressView? progress)
    {
        var items = progress is not null && _projectPlan is { } project
            && ProgressMatches(project, progress)
                ? progress.Items : null;
        foreach (var view in _planItemStatuses.Values) {
            var item = items?[view.Position];
            var (surface, foreground, symbol) = item?.State switch {
                PlanItemProgress.Running => ("Brush.Primary.Surface", "Brush.Primary", (WpfSymbolRegular?)null),
                PlanItemProgress.Succeeded => ("Brush.Success", "Brush.Text.Inverse", WpfSymbolRegular.Checkmark16),
                PlanItemProgress.Failed => ("Brush.Error", "Brush.Text.Inverse", WpfSymbolRegular.Dismiss16),
                PlanItemProgress.Stopped or PlanItemProgress.NotRun => ("Brush.Surface.Disabled", "Brush.Text.Muted",
                    WpfSymbolRegular.Subtract16),
                _ => ("Brush.Surface.Disabled", "Brush.Text.Secondary", null)
            };
            view.Badge.Background = (WpfBrush)FindResource(surface);
            view.Number.Foreground = view.Icon.Foreground = (WpfBrush)FindResource(foreground);
            view.Number.Visibility = symbol is null ? Visibility.Visible : Visibility.Collapsed;
            view.Icon.Visibility = symbol is null ? Visibility.Collapsed : Visibility.Visible;
            view.Icon.Symbol = symbol ?? WpfSymbolRegular.Subtract16;
            view.Status.Text = item is null ? string.Empty : item.State == PlanItemProgress.Running
                ? $"运行中 · {ElapsedText(item.StartedAtUtc, null)}" : RunProgress.StateText(item.State);
            view.Status.Foreground = (WpfBrush)FindResource(item?.State switch {
                PlanItemProgress.Running => "Brush.Primary",
                PlanItemProgress.Succeeded => "Brush.Success",
                PlanItemProgress.Failed => "Brush.Error.Foreground",
                _ => "Brush.Text.Muted"
            });
            view.Status.FontWeight = item?.State is PlanItemProgress.Running or PlanItemProgress.Failed
                ? FontWeights.SemiBold : FontWeights.Normal;
            view.Status.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetItemStatus(view.Badge,
                item is null ? string.Empty : RunProgress.StateText(item.State));
        }
    }

    // Below the reference width the badge shrinks to its icon and a short word; the full reason stays in the tooltip.
    private void UpdateConfigurationLockText()
    {
        var compact = ActualWidth is > 0 and < 1180;
        ConfigurationLockText.Text = compact ? "已锁定" : _configurationLockText;
        ConfigurationLockBadge.ToolTip = compact ? _configurationLockText : null;
        AutomationProperties.SetName(ConfigurationLockBadge, _configurationLockText);
    }

    private void ShowActionableError(string title, Exception exception, string recovery, bool offerLogDirectory)
    {
        var message = $"{exception.GetBaseException().Message}\n\n{recovery}";
        if (!offerLogDirectory) {
            WpfMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var answer = WpfMessageBox.Show(
            $"{message}\n\n是否打开日志目录查看详细信息？",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Error,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) {
            TryOpenLogsDirectory();
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) {
                return result;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) {
                return descendant;
            }
        }

        return null;
    }
}
