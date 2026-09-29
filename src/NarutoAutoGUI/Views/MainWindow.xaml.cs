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
using System.Windows.Media.Imaging;
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
using WpfToolTip = System.Windows.Controls.ToolTip;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WpfNavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using WpfSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace NarutoAutoGUI.Views;

public partial class MainWindow : FluentWindow
{
    private const int MaximumGuiLogEntries = 1000;
    private HwndSource? _previewWindowSource;
    private const string PlanItemDragDataFormat = "NarutoAutoGUI.PlanItem";
    // Busy status prefixes that also drive preview gating, the runtime header and the configuration lock text.
    private const string PreparingEnvironmentStatus = "正在准备运行环境";
    private const string StartingRunStatus = "正在开始任务";
    private const string StoppingRunStatus = "正在停止任务";

    private enum MainSection
    {
        Home,
        Settings
    }

    private readonly AppLogger _logger;
    private readonly ApplicationSettings _settings;
    private readonly string _applicationDirectory;
    private readonly ChildSessionManager _sessionManager;
    private readonly ChildSessionProgramService _programService;
    private readonly WorkerCoordinator _workerCoordinator;
    private readonly Func<Func<Task>, Task> _runApplicationOperationAsync;
    private readonly Func<Task> _requestExitAsync;
    private ChildSessionSnapshot _sessionSnapshot = ChildSessionSnapshot.Empty;
    private WorkerCoordinatorSnapshot _workerSnapshot = WorkerCoordinatorSnapshot.Empty;
    private ProjectPlanModule? _projectPlan;
    private RunStartAttempt? _pendingStartAttempt;
    private ScrollViewer? _homeLogScrollViewer;
    private CancellationTokenSource? _previewPollingCancellation;
    private Task? _previewPollingTask;
    private Guid? _previewWorkerInstanceId;
    private int _previewPollingGeneration;
    private volatile bool _previewPaused;
    private bool _allowClose;
    private bool _busy;
    private string _operationStatus = string.Empty;
    private bool _environmentPreparationFailed;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _projectConfigurationValid;
    private bool _updatingOptionEditors;
    private bool _taskShelfExpanded;
    private readonly Dictionary<string, bool> _taskGroupExpanded = new(StringComparer.Ordinal);
    private bool _navigationPreferenceLoaded;
    private string? _expandedTaskName;
    private string? _dragTaskName;
    private WpfPoint _dragStartPoint;
    private IInputElement? _descriptionDrawerPreviousFocus;
    private readonly List<Border> _dropIndicators = [];
    private readonly Dictionary<string, Border> _planItemContainers = new(StringComparer.Ordinal);
    private readonly List<(TextBlock Summary, string TaskName)> _planItemSummaries = [];

    private readonly Dictionary<(Guid, string, string), (string Text, string Message)> _invalidInputDrafts = new();
    private readonly Dictionary<(Guid, string, string), WpfTextBox> _optionInputEditors = new();
    private System.Windows.Data.Binding? _configurationEditableBinding;
    private static readonly string[] SwitchOnCaseNames = ["Yes", "Y", "On", "True", "Enable", "Enabled"];

    // Edit controls bind IsEnabled to this, so a run lock blocks edits while scrolling, expanding and help stay usable.
    private static readonly DependencyProperty ConfigurationEditableProperty = DependencyProperty.Register(
        "ConfigurationEditable", typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    private sealed record OptionInputTag(Guid ConfigurationId, string OptionName, string InputName, string Value,
        TextBlock Error, string? PatternMessage, bool Submitted = false)
    {
        internal (Guid, string, string) Key => (ConfigurationId, OptionName, InputName);
    }

    // One parameter tile: an input of an input option, or a whole select/switch option.
    private sealed record ParameterField(
        ProjectOptionEditor Option, ProjectInputEditor? Input, string Label, string Description);

    internal MainWindow(
        AppLogger logger,
        ChildSessionManager sessionManager, ChildSessionProgramService programService,
        WorkerCoordinator workerCoordinator, Func<Func<Task>, Task> runApplicationOperationAsync,
        Func<Task> requestExitAsync, string? applicationDirectory = null,
        Func<CancellationToken, Task<EngineCheckResult>>? checkForUpdate = null)
    {
        InitializeComponent();
        DataContext = this;
        BindConfigurationEditable(ConfigurationTabs);
        BindConfigurationEditable(NewConfigurationButton);
        _logger = logger;
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _checkForUpdate = checkForUpdate ?? CheckWithEngineAsync;
        _settings = new ApplicationSettings(_applicationDirectory, logger,
            CheckUpdateFromSettingsAsync, ExportDiagnosticsFromSettingsAsync, ReplayOnboarding, OpenAfdianAsync);
        SettingsView.DataContext = _settings.Page;
        _sessionManager = sessionManager;
        _programService = programService;
        _workerCoordinator = workerCoordinator;
        _runApplicationOperationAsync = runApplicationOperationAsync;
        _requestExitAsync = requestExitAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        HomeLogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _sessionManager.StateChanged += OnSessionStateChanged;
        _workerCoordinator.StateChanged += OnWorkerStateChanged;
        _workerCoordinator.LogReceived += OnWorkerLogReceived;
        _workerSnapshot = workerCoordinator.Snapshot;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        StateChanged += MainWindow_StateChanged;
        SwitchSection(MainSection.Home);
        UpdateCommandAvailability();
    }

    public ObservableCollection<LogEntry> LogLines { get; } = [];

    internal event EventHandler? HiddenToTray;

    internal void AllowClose() => _allowClose = true;

    internal void SetExitInProgress(bool exitInProgress)
    {
        _exitInProgress = exitInProgress;
        if (exitInProgress) {
            EndOnboarding(handled: false);
        }
        UpdateUpdaterControls();
        UpdatePreviewPolling();
        UpdateCommandAvailability();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await InitializeStartupAsync(RestoreExistingSessionAsync);
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
            UpdateCommandAvailability();
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
            "正在恢复已有桌面分身...",
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
            RemovePreviewWindowHook();
            _updateCancellation?.Cancel();
            StopPreviewPolling();
            _sessionManager.StateChanged -= OnSessionStateChanged;
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
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

    private void MainNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_navigationPreferenceLoaded) {
            try {
                if (File.Exists(NavigationPreferencePath)) {
                    MainNavigation.IsPaneOpen = File.ReadAllText(NavigationPreferencePath) != "false";
                }
            } catch (Exception exception) {
                _logger.Warn("读取侧栏展开状态失败，保留默认状态。", exception);
            }
            _navigationPreferenceLoaded = true;
            MainNavigation.PaneOpened += MainNavigation_PaneStateChanged;
            MainNavigation.PaneClosed += MainNavigation_PaneStateChanged;
        }
        if (MainNavigation.Template.FindName("PART_ToggleButton", MainNavigation)
            is Wpf.Ui.Controls.Button { Content: TextBlock title }) {
            // WPF-UI 4.3 places the pane title 6 DIP to the right of navigation item labels.
            title.Margin = new Thickness(-6, 0, 0, 0);
        }
    }

    private string NavigationPreferencePath => Path.Combine(_applicationDirectory, "config", "navigation-pane.txt");

    private void MainNavigation_PaneStateChanged(object sender, RoutedEventArgs e)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(NavigationPreferencePath)!);
            File.WriteAllText(NavigationPreferencePath, MainNavigation.IsPaneOpen ? "true" : "false");
        } catch (Exception exception) {
            _logger.Warn("保存侧栏展开状态失败。", exception);
        }
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfNavigationViewItem { Tag: string sectionName }
            && Enum.TryParse(sectionName, ignoreCase: false, out MainSection section)) {
            SwitchSection(section);
        }
    }

    private void SwitchSection(MainSection section)
    {
        CloseTaskDescriptionDrawer();
        HomeView.Visibility = section == MainSection.Home
            ? Visibility.Visible
            : Visibility.Collapsed;
        RuntimeSidebarVisibility(section);
        SettingsView.Visibility = section == MainSection.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;

        HomeNavigationItem.IsActive = section == MainSection.Home;
        SettingsNavigationItem.IsActive = section == MainSection.Settings;
        UpdatePreviewPolling();
        ReevaluateOnboarding();
    }

    private async void ShowSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(
            "正在显示子桌面...",
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

    private void HomeDesktopVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionSnapshot.State == ChildSessionState.ConnectedVisible) {
            HideSessionButton_Click(sender, e);
        } else if (_sessionSnapshot.State == ChildSessionState.ConnectedHidden) {
            ShowSessionButton_Click(sender, e);
        }
    }

    private void RuntimeSidebarVisibility(MainSection section)
    {
        RuntimeSidebar.Visibility = section == MainSection.Home ? Visibility.Visible : Visibility.Collapsed;
        RuntimeSidebarColumn.Width = section == MainSection.Home
            ? new GridLength(392)
            : new GridLength(0);
    }

    private async void PrepareEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _exitInProgress) {
            return;
        }
        _environmentPreparationFailed = false;
        await RunOperationAsync(
            $"{PreparingEnvironmentStatus}...",
            async () =>
            {
                try {
                    LoadProject();
                    var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                    await _workerCoordinator.PrepareWorkerAsync(sessionId, RequireProject());
                    var game = NarutoGameLaunchProfile.ResolveExisting(_logger);
                    await _programService.LaunchIfNeededAsync(
                        sessionId, game.ExecutablePath, game.Arguments,
                        launchedProcessName: NarutoGameLaunchProfile.ClientProcessName);
                    _sessionManager.ShowPreview();
                    _logger.Info("真实 E2E 环境已准备；完成游戏登录后即可开始任务。 ");
                } catch {
                    _environmentPreparationFailed = true;
                    throw;
                }
            });
    }

    private void RetryRuntimeHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_environmentPreparationFailed || !IsRunReadyToStart) {
            PrepareEnvironmentButton_Click(sender, e);
        } else {
            StartRunButton_Click(sender, e);
        }
    }

    private async void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitFocusedConfigurationInput() || RevealInvalidInputDraft()) {
            return;
        }
        if (!CanStartRun) {
            return;
        }
        await RunOperationAsync(
            $"{StartingRunStatus}...",
            async () =>
            {
                if (!SessionConnected) {
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
        if (!CanStopRun) {
            return;
        }
        await RunOperationAsync(
            $"{StoppingRunStatus}...",
            async () =>
            {
                var activeRun = _workerSnapshot.WorkerSnapshot?.ActiveRun
                                ?? throw new InvalidOperationException("Worker 当前没有 active Run。 ");
                if (!IsStoppable(activeRun)) {
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

    private void SetTaskShelfExpanded(bool expanded)
    {
        if (!expanded) {
            SetTaskSearchVisible(false);
        }
        _taskShelfExpanded = expanded;
        TaskShelfContent.Visibility = _taskShelfExpanded ? Visibility.Visible : Visibility.Collapsed;
        TaskShelfHeaderButton.IsChecked = _taskShelfExpanded;
        AutomationProperties.SetHelpText(
            TaskShelfHeaderButton, _taskShelfExpanded ? "可用任务已展开" : "可用任务已收起");
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditConfiguration || sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            if (!RequireProject().AddTask(task.Name)) {
                return;
            }
            _expandedTaskName = task.Name;
            _pendingStartAttempt = null;
            RenderTaskPlan();
            _logger.Info($"已添加执行计划任务：{task.Name}。 ");
        } catch (Exception exception) {
            HandleProjectEditError("添加执行计划任务失败", exception);
        }
    }

    private void LocatePlanItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: ProjectTaskChoice task }
            && _planItemContainers.TryGetValue(task.Name, out var card)) {
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
        var match = project.SelectedTaskNames.OrderBy(name => name == _expandedTaskName ? 0 : 1)
            .SelectMany(taskName => EnumerateFields(GetConfigurationOrEmpty(taskName))
                .Where(field => field.Input is not null)
                .Select(field => (Task: taskName, Option: field.Option.Name,
                    Key: (project.ActiveConfigurationId, field.Option.Name, field.Input!.Name))))
            .FirstOrDefault(item => _invalidInputDrafts.ContainsKey(item.Key));
        if (match.Task is null) {
            return false;
        }
        if (_expandedTaskName != match.Task || !_optionInputEditors.ContainsKey(match.Key)) {
            _expandedTaskName = match.Task;
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

    private void PlanItemHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        _expandedTaskName = _expandedTaskName == task.Name ? null : task.Name;
        RenderTaskPlan();
    }

    private void RemovePlanItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditConfiguration || sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            if (!RequireProject().RemoveTask(task.Name)) {
                return;
            }
            if (_expandedTaskName == task.Name) {
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
        if (e.Key == Key.Escape && PreviewOverlay.Visibility == Visibility.Visible) {
            ClosePreview_Click(sender, e);
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

    // Esc abandons an unsaved or invalid edit and puts the saved value back.
    private void OptionInputTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not WpfTextBox { Tag: OptionInputTag tag } editor
            || editor.Text == tag.Value && !_invalidInputDrafts.ContainsKey(tag.Key)) {
            return;
        }
        _invalidInputDrafts.Remove(tag.Key);
        editor.Text = tag.Value;
        editor.SelectAll();
        ShowOptionInputError(editor, tag.Error, null);
        RefreshParameterSummaries();
        e.Handled = true;
    }

    private void ShowOptionInputError(WpfTextBox editor, TextBlock error, string? message)
    {
        if (message is null) {
            editor.ClearValue(BorderBrushProperty);
            error.Visibility = Visibility.Collapsed;
        } else {
            editor.BorderBrush = (WpfBrush)FindResource("Brush.Error");
            error.Text = message;
            error.Visibility = Visibility.Visible;
        }
        AutomationProperties.SetItemStatus(editor, message ?? string.Empty);
    }

    private void SaveSelectedCase(Guid configurationId, string optionName, string caseName, FrameworkElement selector)
    {
        if (_updatingOptionEditors || !CanEditConfiguration) {
            return;
        }

        try {
            var before = GetExpandedOptions();
            _ = RequireProject().SetSelectedCase(configurationId, optionName, caseName);
            _pendingStartAttempt = null;
            var after = GetExpandedOptions();
            var optionsChanged = !before.Select(item => item.Name).SequenceEqual(after.Select(item => item.Name));
            if (!optionsChanged) {
                selector.ToolTip = GetCaseToolTip(after.First(item => item.Name == optionName));
            }
            RefreshAfterOptionSaved(optionsChanged);
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
            UpdateCommandAvailability();
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
        UpdateCommandAvailability();
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
            RefreshParameterSummaries();
        }
        RefreshConfigurationValidity(project);
    }

    private ProjectOptionEditor[] GetExpandedOptions() => _expandedTaskName is not { } taskName ? []
        : EnumerateOptions(GetConfigurationOrEmpty(taskName)).ToArray();

    private void RenderAvailableTaskShelf(ProjectPlanModule project)
    {
        var query = TaskSearchBox.Text.Trim();
        var tasks = project.Tasks.Where(task => task.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || task.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        AvailableTaskCountText.Text = query.Length == 0
            ? project.Tasks.Count.ToString() : $"{tasks.Length} / {project.Tasks.Count}";
        AvailableTasksPanel.Children.Clear();
        if (tasks.Length == 0) {
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
            var added = project.SelectedTaskNames.Contains(task.Name, StringComparer.Ordinal);
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

    private void TaskSearchToggleButton_Click(object sender, RoutedEventArgs e)
        => SetTaskSearchVisible(TaskSearchBox.Visibility != Visibility.Visible);

    private void SetTaskSearchVisible(bool visible)
    {
        TaskSearchBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TaskSearchToggleIcon.Symbol = visible ? WpfSymbolRegular.Dismiss16 : WpfSymbolRegular.Search16;
        TaskSearchToggleButton.ToolTip = visible ? "关闭搜索（Esc）" : "搜索任务";
        AutomationProperties.SetName(TaskSearchToggleButton, visible ? "关闭任务搜索" : "展开任务搜索");
        if (visible) {
            SetTaskShelfExpanded(true);
            TaskSearchBox.Focus();
        } else {
            TaskSearchBox.Clear();
        }
    }

    private void TaskSearchBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape) {
            SetTaskSearchVisible(false);
            TaskSearchToggleButton.Focus();
            e.Handled = true;
        }
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
        TaskShelfContent.MaxHeight = Math.Min(280, Math.Max(0, e.NewSize.Height / 3));
    }

    private void RenderPlanItems(ProjectPlanModule project)
    {
        PlanItemsPanel.Children.Clear();
        _dropIndicators.Clear();
        _planItemContainers.Clear();
        _planItemSummaries.Clear();
        _taskDescriptionButtons.Clear();
        _optionInputEditors.Clear();
        EmptyPlanPanel.Visibility = project.SelectedTaskNames.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var taskName in project.SelectedTaskNames) {
            AddDropIndicator();
            var task = project.Tasks.SingleOrDefault(candidate => candidate.Name == taskName)
                ?? new ProjectTaskChoice(taskName, taskName, "此任务已不在当前项目中，可从配置中移除。");
            var expanded = _expandedTaskName == task.Name;
            var container = CreatePlanItem(task, expanded);
            _planItemContainers.Add(task.Name, container);
            PlanItemsPanel.Children.Add(container);
        }
        AddDropIndicator();
    }

    private Task OpenAfdianAsync()
    {
        const string address = "https://afdian.com/a/archersore";
        try {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            _settings.OpenAfdian.Status = string.Empty;
        } catch (Exception exception) {
            _settings.OpenAfdian.Status = "无法打开爱发电，请检查默认浏览器设置。";
            _logger.Warn("打开爱发电赞助页面失败。", exception);
            WpfMessageBox.Show(this, $"请手动访问：{address}\n\n按 Ctrl+C 可复制此提示中的网址。",
                "无法打开爱发电", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return Task.CompletedTask;
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

    private ProjectConfigurationView GetConfigurationOrEmpty(string taskName)
    {
        try {
            return RequireProject().GetConfiguration(taskName);
        } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) {
            return new ProjectConfigurationView([], []);
        }
    }

    private Border CreatePlanItem(ProjectTaskChoice task, bool expanded)
    {
        var configuration = GetConfigurationOrEmpty(task.Name);
        var hasParameters = EnumerateFields(configuration).Any();
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

        layout.Children.Add(CreatePlanItemHeader(task, expanded, hasParameters, configuration));

        if (expanded && hasParameters) {
            var editor = CreateParameterEditor(configuration);
            var editorBorder = new Border {
                Margin = new Thickness(12, 0, 12, 10),
                Padding = new Thickness(0, 8, 0, 0),
                BorderBrush = (WpfBrush)FindResource("Brush.Border.Subtle"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = editor
            };
            Grid.SetRow(editorBorder, 1);
            layout.Children.Add(editorBorder);
        }
        container.Child = layout;
        return container;
    }

    private Grid CreatePlanItemHeader(ProjectTaskChoice task, bool expanded, bool hasParameters,
        ProjectConfigurationView configuration)
    {
        var header = new Grid {
            MinHeight = 44,
            Margin = new Thickness(expanded ? 6 : 4, 0, 4, 0)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dragHandle = CreateIconButton(WpfSymbolRegular.ReOrderDotsVertical20, "拖动以调整顺序");
        dragHandle.Tag = task;
        dragHandle.Cursor = WpfCursors.SizeAll;
        dragHandle.PreviewMouseLeftButtonDown += PlanDragHandle_PreviewMouseLeftButtonDown;
        dragHandle.PreviewMouseMove += PlanDragHandle_PreviewMouseMove;
        BindConfigurationEditable(dragHandle);
        header.Children.Add(dragHandle);

        var label = new TextBlock {
            Text = task.Label,
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
            _planItemSummaries.Add((summaryText, task.Name));
            FillParameterSummary(summaryText, configuration);
            Grid.SetColumn(summaryText, 2);
            title.Children.Add(summaryText);
        }
        var main = new WpfButton {
            Tag = task,
            Content = showSummary ? title : label,
            Style = (Style)FindResource("PlanHeaderButtonStyle")
        };
        var state = hasParameters ? (expanded ? "已展开" : "已折叠") : (expanded ? "已选中" : "未选中");
        AutomationProperties.SetName(main, $"{task.Label}，{state}");
        AutomationProperties.SetHelpText(main, hasParameters
            ? (expanded ? "点击折叠参数" : "点击展开参数") : (expanded ? "点击取消选中" : "点击选中任务"));
        main.Click += PlanItemHeader_Click;
        Grid.SetColumn(main, 1);
        header.Children.Add(main);

        if (!string.IsNullOrWhiteSpace(task.Description)) {
            var information = CreateIconButton(WpfSymbolRegular.Info16, $"查看 {task.Label} 的任务描述");
            information.Tag = task;
            information.Click += TaskDescriptionButton_Click;
            _taskDescriptionButtons[task.Name] = information;
            Grid.SetColumn(information, 2);
            header.Children.Add(information);
        }

        var remove = CreateIconButton(WpfSymbolRegular.Dismiss16, $"从执行计划移除 {task.Label}");
        remove.Tag = task;
        remove.Click += RemovePlanItemButton_Click;
        BindConfigurationEditable(remove);
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);
        return header;
    }

    // An unfixed invalid draft replaces the saved value in red, so folded cards agree with the field and Start block.
    private void FillParameterSummary(TextBlock summary, ProjectConfigurationView configuration)
    {
        var configurationId = RequireProject().ActiveConfigurationId;
        summary.Inlines.Clear();
        foreach (var field in EnumerateFields(configuration)) {
            if (summary.Inlines.Count != 0) {
                summary.Inlines.Add(new System.Windows.Documents.Run(" · "));
            }
            var run = new System.Windows.Documents.Run();
            if (field.Input is not { } input) {
                var selected = field.Option.Cases.Single(item => item.Name == field.Option.SelectedCase);
                run.Text = $"{field.Label}：{selected.Label}";
            } else if (_invalidInputDrafts.TryGetValue(
                (configurationId, field.Option.Name, input.Name), out var draft)) {
                run.Text = $"{field.Label}：{draft.Text}";
                run.Foreground = (WpfBrush)FindResource("Brush.Error.Foreground");
            } else {
                run.Text = $"{field.Label}：{input.Value}";
            }
            summary.Inlines.Add(run);
        }
    }

    // Drafts change without saving, so only the folded summaries are refreshed; the focused editor stays in place.
    private void RefreshParameterSummaries()
    {
        foreach (var (summary, taskName) in _planItemSummaries) {
            FillParameterSummary(summary, GetConfigurationOrEmpty(taskName));
        }
    }

    private FrameworkElement CreateParameterEditor(ProjectConfigurationView configuration)
    {
        var panel = new ResponsiveWrapPanel();
        foreach (var field in EnumerateFields(configuration)) {
            panel.Children.Add(field.Input is { } input ? CreateInputEditor(field, input) : CreateCaseEditor(field));
        }
        return panel;
    }

    private Border CreateInputEditor(ParameterField parameter, ProjectInputEditor input)
    {
        var content = new StackPanel();
        content.Children.Add(CreateParameterLabel(parameter.Label, parameter.Description));
        // MaxWidth is the minimum tile width minus padding, so a long hint never forces a full-width row.
        var error = new TextBlock {
            Margin = new Thickness(0, 4, 0, 0), MaxWidth = 212,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("SecondaryTextStyle"),
            Foreground = (WpfBrush)FindResource("Brush.Error.Foreground"), Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var tag = new OptionInputTag(
            _projectPlan!.ActiveConfigurationId, parameter.Option.Name, input.Name, input.Value, error,
            input.PatternMessage);
        var editor = new Wpf.Ui.Controls.TextBox {
            Margin = new Thickness(0, 5, 0, 0),
            Style = (Style)FindResource("Option.TextBox"),
            Text = input.Value,
            Tag = tag,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ToolTip = input.PatternMessage
        };
        AutomationProperties.SetName(editor, parameter.Label);
        if (!string.IsNullOrWhiteSpace(parameter.Description)) {
            AutomationProperties.SetHelpText(editor, parameter.Description);
        }
        editor.LostKeyboardFocus += OptionInputTextBox_LostKeyboardFocus;
        editor.PreviewKeyDown += OptionInputTextBox_PreviewKeyDown;
        BindConfigurationEditable(editor);
        content.Children.Add(editor);
        content.Children.Add(error);
        _optionInputEditors[tag.Key] = editor;
        if (_invalidInputDrafts.TryGetValue(tag.Key, out var draft)) {
            editor.Text = draft.Text;
            ShowOptionInputError(editor, error, draft.Message);
        }
        return new Border { Style = (Style)FindResource("OptionTileStyle"), Child = content };
    }

    private Border CreateCaseEditor(ParameterField parameter)
    {
        var option = parameter.Option;
        var content = new StackPanel();
        content.Children.Add(CreateParameterLabel(parameter.Label, parameter.Description));
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
                SaveSelectedCase(configurationId, option.Name, toggle.IsChecked == true ? on.Name : off.Name, toggle);
            selector = toggle;
            field = new Grid { Height = (double)FindResource("Height.OptionEditor"), Children = { toggle } };
        } else {
            var comboBox = new WpfComboBox {
                ItemsSource = option.Cases,
                DisplayMemberPath = nameof(ProjectCaseEditor.Label),
                SelectedItem = option.Cases.Single(item => item.Name == option.SelectedCase),
                Style = (Style)FindResource("Option.ComboBox"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            comboBox.SelectionChanged += (_, _) => {
                if (comboBox.SelectedItem is ProjectCaseEditor selected) {
                    SaveSelectedCase(configurationId, option.Name, selected.Name, comboBox);
                }
            };
            selector = comboBox;
            field = comboBox;
        }
        field.Margin = new Thickness(0, 5, 0, 0);
        selector.ToolTip = GetCaseToolTip(option);
        AutomationProperties.SetName(selector, parameter.Label);
        if (!string.IsNullOrWhiteSpace(parameter.Description)) {
            AutomationProperties.SetHelpText(selector, parameter.Description);
        }
        BindConfigurationEditable(selector);
        content.Children.Add(field);
        return new Border { Style = (Style)FindResource("OptionTileStyle"), Child = content };
    }

    private static string GetCaseToolTip(ProjectOptionEditor option) => option.IsExplicit ? "当前值由用户显式设置"
        : $"当前跟随项目默认：{option.Cases.Single(item => item.Name == option.DefaultCase).Label}";

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

    private Grid CreateParameterLabel(string label, string? description)
    {
        // Same height with or without the info button, so editors in one row start on the same line.
        var header = new Grid { MinHeight = 20 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            FontSize = (double)FindResource("FontSize.Label"),
            Foreground = (WpfBrush)FindResource("Brush.Text.Primary"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(description)) {
            var information = CreateDescriptionInfoButton(description, $"{label} 的说明");
            information.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(information, 1);
            header.Children.Add(information);
        }
        return header;
    }

    private WpfButton CreateDescriptionInfoButton(string description, string accessibleName)
    {
        var toolTip = new WpfToolTip { Content = description, MaxWidth = 320 };
        var button = CreateIconButton(WpfSymbolRegular.Info12, accessibleName);
        button.MinWidth = 20;
        button.MinHeight = 20;
        button.Padding = new Thickness(2);
        button.ToolTip = toolTip;
        ToolTipService.SetInitialShowDelay(button, 250);
        button.GotKeyboardFocus += (_, _) => toolTip.IsOpen = true;
        button.LostKeyboardFocus += (_, _) => toolTip.IsOpen = false;
        button.Click += (_, args) =>
        {
            toolTip.IsOpen = !toolTip.IsOpen;
            args.Handled = true;
        };
        return button;
    }

    private WpfButton CreateIconButton(WpfSymbolRegular symbol, string accessibleName)
    {
        var button = new WpfButton {
            Content = CreateSymbolIcon(symbol),
            Style = (Style)FindResource("TaskIconButtonStyle")
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
        if (_planItemContainers.TryGetValue(task.Name, out var container)) {
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
        if (!CanEditConfiguration || e.Data.GetData(PlanItemDragDataFormat) is not string taskName) {
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
            var container = _planItemContainers[project.SelectedTaskNames[index]];
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

    // Labels fall back from input to option to option name; a PI may declare either label as "".
    private static IEnumerable<ParameterField> EnumerateFields(ProjectConfigurationView configuration)
    {
        foreach (var option in EnumerateOptions(configuration)) {
            var label = string.IsNullOrWhiteSpace(option.Label) ? option.Name : option.Label;
            if (option.Kind != ProjectOptionKind.Input) {
                yield return new ParameterField(option, null, label, option.Description);
            } else {
                foreach (var input in option.Inputs) {
                    yield return new ParameterField(option, input,
                        string.IsNullOrWhiteSpace(input.Label) ? label : input.Label,
                        string.IsNullOrWhiteSpace(input.Description) ? option.Description : input.Description);
                }
            }
        }
    }

    private static IEnumerable<ProjectOptionEditor> EnumerateOptions(ProjectConfigurationView configuration) =>
        configuration.GlobalOptions.SelectMany(Flatten)
            .Concat(configuration.TaskOptions.SelectMany(Flatten));

    private static IEnumerable<ProjectOptionEditor> Flatten(ProjectOptionEditor option)
    {
        yield return option;
        foreach (var child in option.ActiveChildren.SelectMany(Flatten)) {
            yield return child;
        }
    }

    private async Task RunOperationAsync(string status, Func<Task> operation)
    {
        if (_busy || _exitInProgress) {
            return;
        }

        SetBusy(true, status);
        try {
            await _runApplicationOperationAsync(operation);
        } catch (OperationCanceledException) {
            _logger.Warn($"操作已取消：{status}");
        } catch (Exception exception) {
            var operationName = status.TrimEnd('.', '…');
            if (operationName.StartsWith("正在", StringComparison.Ordinal)) {
                operationName = operationName[2..];
            }

            HandleOperationError($"{operationName}失败", exception);
        } finally {
            SetBusy(false, string.Empty);
        }
    }

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

    internal static LogEntry? CreateUserFacingRunLogEntry(WorkerLogEntry workerEntry)
    {
        if (!IsUserFacingRunLog(workerEntry)) {
            return null;
        }

        var timestampUtc = DateTime.SpecifyKind(workerEntry.TimestampUtc, DateTimeKind.Utc);
        return new LogEntry(
            new DateTimeOffset(timestampUtc).ToLocalTime(),
            ParseWorkerLogLevel(workerEntry.Level),
            workerEntry.Message);
    }

    internal static void WriteWorkerDiagnosticLog(AppLogger logger, WorkerLogEntry entry) =>
        logger.Write(ParseWorkerLogLevel(entry.Level), $"Worker #{entry.Sequence} [{entry.Source}] {entry.Message}");

    private void AddRunLogEntry(WorkerLogEntry workerEntry)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => AddRunLogEntry(workerEntry));
            return;
        }

        var entry = CreateUserFacingRunLogEntry(workerEntry);
        if (entry is null) {
            return;
        }
        var shouldFollow = _followLogs && IsLogNearTop();
        var previousOffset = _homeLogScrollViewer?.VerticalOffset ?? 0;
        LogLines.Insert(0, entry);
        while (LogLines.Count > MaximumGuiLogEntries) {
            LogLines.RemoveAt(LogLines.Count - 1);
        }
        if (!shouldFollow && LogLines.Count > 1) {
            _homeLogScrollViewer?.ScrollToVerticalOffset(previousOffset + 1);
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

        _workerSnapshot = snapshot;
        UpdateCommandAvailability();
        UpdatePreviewPolling();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdatePreviewPolling();
        ReevaluateOnboarding();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdatePreviewPolling();
        ReevaluateOnboarding();
    }

    private void UpdatePreviewPolling()
    {
        var paused = WindowState == WindowState.Minimized;
        if (paused && !_previewPaused) {
            ShowPreviewPlaceholder("等待游戏画面");
        }
        _previewPaused = paused;
        if (!TryGetPreviewTarget(out var workerId)) {
            StopPreviewPolling();
            return;
        }
        if (_previewWorkerInstanceId == workerId && _previewPollingTask is { IsCompleted: false }) {
            return;
        }
        StopPreviewPolling();
        _previewWorkerInstanceId = workerId;
        var generation = _previewPollingGeneration;
        var cancellation = new CancellationTokenSource();
        _previewPollingCancellation = cancellation;
        var client = new LivePreviewClient(_workerCoordinator,
            error => _logger.Warn("Preview 传输失败。", error));
        _previewPollingTask = Task.Run(async () =>
        {
            try {
                await client.RunAsync(workerId,
                    action => Dispatcher.InvokeAsync(action, DispatcherPriority.Background, cancellation.Token).Task,
                    (frame, pixels) =>
                    {
                        if (_previewPaused || _previewPollingGeneration != generation
                            || cancellation.IsCancellationRequested
                            || !TryGetPreviewTarget(out var current) || current != workerId) {
                            return;
                        }
                        if (frame is { State: PreviewState.Streaming, Revision: > 0 }) {
                            DisplayPreviewFrame(frame, pixels);
                        } else {
                            ShowPreviewPlaceholder(frame?.State == PreviewState.WaitingForWindow
                                ? "等待游戏窗口" : "等待游戏画面");
                        }
                    }, cancellation.Token, () => _previewPaused);
            } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            } finally {
                cancellation.Dispose();
            }
        });
    }

    private bool TryGetPreviewTarget(out Guid workerId)
    {
        var worker = _workerSnapshot.WorkerSnapshot;
        var preparing = IsBusyWith(PreparingEnvironmentStatus);
        if (!_exitInProgress && !_environmentPreparationFailed && !preparing && IsVisible
            && HomeView.Visibility == Visibility.Visible
            && (PreviewCardContent.Visibility == Visibility.Visible || PreviewOverlay.Visibility == Visibility.Visible)
            && SessionConnected && WorkerFresh && worker is { WorkerState: WorkerState.Ready }
            && worker.ChildSessionId == _sessionSnapshot.ChildSessionId) {
            workerId = worker.WorkerInstanceId;
            return true;
        }
        workerId = Guid.Empty;
        return false;
    }

    private void StopPreviewPolling()
    {
        _previewPollingGeneration++;
        var cancellation = _previewPollingCancellation;
        _previewPollingCancellation = null;
        _previewPollingTask = null;
        _previewWorkerInstanceId = null;
        try {
            cancellation?.Cancel();
        } catch (ObjectDisposedException) {
            // The background reader may already have completed.
        }
        ShowPreviewPlaceholder();
    }

    private void ExpandPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingActive) {
            return;
        }
        PreviewOverlay.Visibility = Visibility.Visible;
        UpdateExpandedPreviewSize();
        MainNavigation.IsEnabled = false;
        EnsureModalWindowHook();
        PreviewOverlay.Focus();
        UpdatePreviewPolling();
    }

    private bool IsGlobalModalOpen => PreviewOverlay.Visibility == Visibility.Visible
        || UpdateOverlay.Visibility == Visibility.Visible;

    private void EnsureModalWindowHook()
    {
        if (_previewWindowSource is null && PresentationSource.FromVisual(MainWindowContent) is HwndSource source) {
            _previewWindowSource = source;
            source.AddHook(PreviewWindowHook);
        }
    }

    private void PreviewOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateExpandedPreviewSize();

    private void UpdateExpandedPreviewSize()
    {
        if (ExpandedPreviewCard is null || PreviewOverlay.Visibility != Visibility.Visible) {
            return;
        }
        // Card chrome: 48px header, 8px image inset on each side, and 1px outer border.
        var image = HomePreviewImage.Source as BitmapSource;
        var aspectRatio = image is null ? 16.0 / 9 : (double)image.PixelWidth / image.PixelHeight;
        var availableWidth = Math.Max(1, PreviewOverlay.ActualWidth - 80 - 18);
        var availableHeight = Math.Max(1, PreviewOverlay.ActualHeight - 80 - 66);
        var imageWidth = Math.Min(availableWidth, availableHeight * aspectRatio);
        ExpandedPreviewCard.Width = imageWidth + 18;
        ExpandedPreviewCard.Height = imageWidth / aspectRatio + 66;
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        PreviewOverlay.Visibility = Visibility.Collapsed;
        RemovePreviewWindowHook();
        MainNavigation.IsEnabled = true;
        ExpandPreviewButton.Focus();
        UpdatePreviewPolling();
        ReevaluateOnboarding();
    }

    private void RemovePreviewWindowHook()
    {
        _previewWindowSource?.RemoveHook(PreviewWindowHook);
        _previewWindowSource = null;
    }

    private nint PreviewWindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
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

    private void TogglePreview_Click(object sender, RoutedEventArgs e)
        => SetPreviewExpanded(PreviewCardContent.Visibility != Visibility.Visible);

    private void SetPreviewExpanded(bool expanded)
    {
        PreviewCardContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PreviewChevron.Symbol = expanded ? WpfSymbolRegular.ChevronUp16 : WpfSymbolRegular.ChevronDown16;
        UpdatePreviewPolling();
    }

    private void DisplayPreviewFrame(PreviewFrameInfo frame, byte[] pixels)
    {
        if (HomePreviewImage.Source is not WriteableBitmap image
            || image.PixelWidth != frame.Width || image.PixelHeight != frame.Height) {
            image = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            HomePreviewImage.Source = image;
            UpdateExpandedPreviewSize();
        }
        image.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), pixels, frame.Width * 4, 0);
        HomePreviewImage.Visibility = Visibility.Visible;
        HomePreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ShowPreviewPlaceholder(string detail = "请先连接运行环境")
    {
        HomePreviewImage.Source = null;
        HomePreviewPlaceholder.Content = detail;
        UpdateExpandedPreviewSize();
        HomePreviewImage.Visibility = Visibility.Collapsed;
        HomePreviewPlaceholder.Visibility = Visibility.Visible;
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
        LogLines.Clear();
        _followLogs = true;
    }

    private bool IsLogNearTop()
    {
        _homeLogScrollViewer ??= FindVisualChild<ScrollViewer>(HomeLogListBox);
        return _homeLogScrollViewer is null || _homeLogScrollViewer.VerticalOffset <= 0.1;
    }

    private void ScrollLogsToLatest()
    {
        if (_followLogs && LogLines.FirstOrDefault() is LogEntry latestLine) {
            HomeLogListBox.ScrollIntoView(latestLine);
        }
    }

    private void OnSessionStateChanged(object? sender, ChildSessionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, snapshot));
            return;
        }

        _sessionSnapshot = snapshot;
        UpdatePreviewPolling();
        UpdateCommandAvailability();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _operationStatus = status;
        Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        UpdateCommandAvailability();
        UpdatePreviewPolling();
    }

    private bool IsBusyWith(string status) => _busy && _operationStatus.StartsWith(status, StringComparison.Ordinal);

    // Keep the stale snapshot for diagnostics, but a confirmed ended runtime cannot own current controls.
    private WorkerSnapshot? RuntimeControlWorker =>
        _workerSnapshot.Observation is WorkerObservation.ChildSessionEnded or WorkerObservation.WorkerExited
            ? null : _workerSnapshot.WorkerSnapshot;

    // No busy operation, exit, or session connect/disconnect is in progress.
    private bool CanRunCommand => !_busy && !_exitInProgress
        && _sessionSnapshot.State is not (ChildSessionState.Connecting or ChildSessionState.Disconnecting);

    private bool SessionConnected =>
        _sessionSnapshot.State is ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden;

    private bool WorkerFresh => _workerSnapshot is { Observation: WorkerObservation.Connected, SnapshotFresh: true };

    private static bool IsStoppable(RunSnapshot run) => run.State == RunState.Running
        && run.Items.Any(item => item.State is PlanItemState.Starting or PlanItemState.Running);

    // The plan and runtime state allow a run; CanStartRun additionally applies the temporary command gate.
    private bool IsRunReadyToStart => _projectPlan is { SelectedTaskNames.Count: > 0 } project
        && _projectConfigurationValid && SessionConnected && WorkerFresh
        && RuntimeControlWorker is { ActiveRun: null, RunState: RunState.Idle, WorkerState: WorkerState.Ready } worker
        && worker.RuntimeProfileDigest == project.RuntimeProfileDigest;

    private bool CanStartRun => CanRunCommand && IsRunReadyToStart;

    private bool CanStopRun => CanRunCommand && WorkerFresh && _projectPlan is { SelectedTaskNames.Count: > 0 }
        && RuntimeControlWorker?.ActiveRun is { } run && IsStoppable(run);

    private void UpdateCommandAvailability()
    {
        ReevaluateOnboarding();
        var state = _sessionSnapshot.State;
        var canRunCommand = CanRunCommand;
        var sessionConnected = SessionConnected;

        HomeDesktopVisibilityButton.Tag = state == ChildSessionState.ConnectedVisible ? "True" : "False";
        if (sessionConnected) {
            HomeDesktopVisibilityText.Text = state == ChildSessionState.ConnectedVisible
                ? "隐藏分身"
                : "显示分身";
            HomeDesktopVisibilityButton.IsEnabled = canRunCommand;
        } else {
            HomeDesktopVisibilityText.Text = "显示分身";
            HomeDesktopVisibilityButton.IsEnabled = false;
        }

        HomeDesktopVisibilityButton.ToolTip = HomeDesktopVisibilityText.Text;
        AutomationProperties.SetName(HomeDesktopVisibilityButton, HomeDesktopVisibilityText.Text);

        var projectReady = _projectPlan is not null;
        UpdateRuntimeHeader(canRunCommand, projectReady, sessionConnected);
        var editable = CanEditConfiguration;
        SetValue(ConfigurationEditableProperty, editable);
        ConfigurationLockBadge.Visibility = projectReady && !editable ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationLockText.Text = RuntimeControlWorker?.ActiveRun is not null
            || IsBusyWith(StartingRunStatus) ? "任务运行中，配置已锁定" : "运行环境处理中，配置暂时锁定";
    }

    private void UpdateRuntimeHeader(bool canRunCommand, bool projectReady, bool sessionConnected)
    {
        var worker = RuntimeControlWorker;
        var active = worker?.ActiveRun;
        var preparing = IsBusyWith(PreparingEnvironmentStatus)
            || _sessionSnapshot.State is ChildSessionState.Connecting or ChildSessionState.Existing
            || _workerSnapshot.Observation == WorkerObservation.WorkerStarting
            || worker?.WorkerState == WorkerState.Starting;
        var runtimeFaulted = _environmentPreparationFailed || _sessionSnapshot.State == ChildSessionState.Faulted
            || _workerSnapshot.Observation is WorkerObservation.IpcDisconnected or WorkerObservation.WorkerExited
                or WorkerObservation.WorkerRecoveryConflict
            || worker?.WorkerState == WorkerState.Faulted;
        var runFaulted = active is null && worker?.LastRun?.State == RunState.Failed;
        var ready = sessionConnected && projectReady && WorkerFresh && worker?.WorkerState == WorkerState.Ready
            && worker.RuntimeProfileDigest == _projectPlan!.RuntimeProfileDigest;
        var running = active?.State is RunState.Starting or RunState.Running or RunState.Stopping;
        var faulted = !running && (runtimeFaulted || runFaulted);
        var starting = active?.State == RunState.Starting || IsBusyWith(StartingRunStatus);
        var stopping = active?.State == RunState.Stopping || IsBusyWith(StoppingRunStatus);
        var transitioning = preparing || starting || stopping;

        PrepareEnvironmentButton.Visibility = !transitioning && !running && !faulted && !ready
            ? Visibility.Visible : Visibility.Collapsed;
        PrepareEnvironmentButton.IsEnabled = canRunCommand && projectReady;
        RetryEnvironmentButton.Visibility = !transitioning && faulted ? Visibility.Visible : Visibility.Collapsed;
        RetryEnvironmentButton.IsEnabled = canRunCommand && projectReady;
        StartTaskHeaderButton.Visibility = !transitioning && !running && ready && !faulted
            ? Visibility.Visible : Visibility.Collapsed;
        StartTaskHeaderButton.IsEnabled = CanStartRun;
        StopTaskHeaderButton.Visibility = !transitioning && active?.State == RunState.Running
            ? Visibility.Visible : Visibility.Collapsed;
        StopTaskHeaderButton.IsEnabled = CanStopRun;
        RuntimeHeaderProgressRing.Visibility = transitioning ? Visibility.Visible : Visibility.Collapsed;
        var progressText = stopping ? StoppingRunStatus : starting ? StartingRunStatus : PreparingEnvironmentStatus;
        RuntimeHeaderProgressRing.ToolTip = progressText;
        System.Windows.Automation.AutomationProperties.SetName(RuntimeHeaderProgressRing, progressText);
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
