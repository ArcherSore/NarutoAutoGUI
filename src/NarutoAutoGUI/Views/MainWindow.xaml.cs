using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
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
using WpfSize = System.Windows.Size;
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

    private enum MainSection
    {
        Home,
        Settings
    }

    private enum PrimaryActionMode
    {
        ConfigureTasks,
        Prepare,
        Start,
        Stop,
        Transition
    }

    private sealed record PrimaryActionState(PrimaryActionMode Mode, bool CanExecute);
    private readonly AppLogger _logger;
    private readonly ChildSessionManager _sessionManager;
    private readonly ChildSessionProgramService _programService;
    private readonly WorkerCoordinator _workerCoordinator;
    private readonly Func<Func<Task>, Task> _runApplicationOperationAsync;
    private readonly Func<Task> _requestExitAsync;
    private readonly DispatcherTimer _elapsedTimer;
    private ChildSessionSnapshot _sessionSnapshot = ChildSessionSnapshot.Empty;
    private WorkerCoordinatorSnapshot _workerSnapshot = WorkerCoordinatorSnapshot.Empty;
    private ProjectPlanModule? _projectPlan;
    private RunStartAttempt? _pendingStartAttempt;
    private ScrollViewer? _homeLogScrollViewer;
    private CancellationTokenSource? _previewPollingCancellation;
    private Task? _previewPollingTask;
    private Guid? _previewWorkerInstanceId;
    private int _previewPollingGeneration;
    private bool _allowClose;
    private bool _busy;
    private string _operationStatus = string.Empty;
    private bool _environmentPreparationFailed;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _projectConfigurationValid;
    private bool _updatingOptionEditors;
    private bool _taskShelfExpanded = true;
    private string? _expandedTaskName;
    private string? _dragTaskName;
    private WpfPoint _dragStartPoint;
    private IInputElement? _descriptionDrawerPreviousFocus;
    private readonly List<Border> _dropIndicators = [];
    private readonly Dictionary<string, Border> _planItemContainers = new(StringComparer.Ordinal);

    private sealed record OptionInputTag(
        Guid ConfigurationId, string OptionName, string InputName, string Value, bool Submitted = false);

    private sealed record OptionCaseTag(Guid ConfigurationId, string OptionName);

    internal MainWindow(
        AppLogger logger,
        ChildSessionManager sessionManager, ChildSessionProgramService programService,
        WorkerCoordinator workerCoordinator, Func<Func<Task>, Task> runApplicationOperationAsync,
        Func<Task> requestExitAsync)
    {
        InitializeComponent();
        DataContext = this;
        _logger = logger;
        _sessionManager = sessionManager;
        _programService = programService;
        _workerCoordinator = workerCoordinator;
        _runApplicationOperationAsync = runApplicationOperationAsync;
        _requestExitAsync = requestExitAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal) {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += ElapsedTimer_Tick;
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
        UpdateWorkerPresentation(_workerSnapshot);
        UpdateCommandAvailability();
    }

    public ObservableCollection<LogEntry> LogLines { get; } = [];

    internal event EventHandler? HiddenToTray;

    internal void AllowClose() => _allowClose = true;

    internal void SetExitInProgress(bool exitInProgress)
    {
        _exitInProgress = exitInProgress;
        UpdateUpdaterControls();
        UpdatePreviewPolling();
        UpdateCommandAvailability();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
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
            RemovePreviewWindowHook();
            _updateCancellation?.Cancel();
            _elapsedTimer.Stop();
            StopPreviewPolling();
            _sessionManager.StateChanged -= OnSessionStateChanged;
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
            _workerCoordinator.LogReceived -= OnWorkerLogReceived;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            StateChanged -= MainWindow_StateChanged;
            return;
        }

        e.Cancel = true;
        Hide();
        _logger.Info("主窗口已隐藏到托盘。");
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void MainNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        if (MainNavigation.Template.FindName("PART_ToggleButton", MainNavigation)
            is Wpf.Ui.Controls.Button { Content: TextBlock title }) {
            // WPF-UI 4.3 places the pane title 6 DIP to the right of navigation item labels.
            title.Margin = new Thickness(-6, 0, 0, 0);
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

    private async void TerminateSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            "结束桌面分身将注销 Session，并结束其中运行的程序。确认继续吗？",
            "结束桌面分身", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) {
            return;
        }

        await RunOperationAsync(
            "正在结束桌面分身...",
            async () =>
            {
                await _sessionManager.TerminateAsync();
                _workerCoordinator.ChildSessionEnded();
            });
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        => TryOpenLogsDirectory();

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

    private void HomeSessionMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button && button.ContextMenu is not null) {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
            button.ContextMenu.CustomPopupPlacementCallback = PlaceSessionMenu;
            button.ContextMenu.IsOpen = true;
        }
    }

    private static System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceSessionMenu(
        WpfSize popupSize, WpfSize targetSize, WpfPoint offset)
    {
        var point = new WpfPoint(targetSize.Width - popupSize.Width, targetSize.Height + 2);
        return [new(point, System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
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
            "正在准备运行环境...",
            async () =>
            {
                try {
                    LoadProject();
                    var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                    await _workerCoordinator.PrepareWorkerAsync(
                        sessionId, _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"));
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
        if (_environmentPreparationFailed || DerivePrimaryAction().Mode != PrimaryActionMode.Start) {
            PrepareEnvironmentButton_Click(sender, e);
        } else {
            StartRunButton_Click(sender, e);
        }
    }

    private async void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitFocusedConfigurationInput()) {
            return;
        }
        if (DerivePrimaryAction() is not { Mode: PrimaryActionMode.Start, CanExecute: true }) {
            return;
        }
        await RunOperationAsync(
            "正在开始任务...",
            async () =>
            {
                if (_sessionSnapshot.State is not (ChildSessionState.ConnectedVisible
                    or ChildSessionState.ConnectedHidden)) {
                    throw new InvalidOperationException("Child Session 尚未连接，当前不能开始任务。 ");
                }
                var project = _projectPlan
                              ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
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
        if (DerivePrimaryAction() is not { Mode: PrimaryActionMode.Stop, CanExecute: true }) {
            return;
        }
        await RunOperationAsync(
            "正在停止任务...",
            async () =>
            {
                var activeRun = _workerSnapshot.WorkerSnapshot?.ActiveRun
                                ?? throw new InvalidOperationException("Worker 当前没有 active Run。 ");
                if (activeRun.State != RunState.Running
                    || !activeRun.Items.Any(item => item.State is PlanItemState.Starting or PlanItemState.Running)) {
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
    {
        _taskShelfExpanded = !_taskShelfExpanded;
        TaskShelfContent.Visibility = _taskShelfExpanded ? Visibility.Visible : Visibility.Collapsed;
        TaskShelfChevronIcon.Symbol = _taskShelfExpanded
            ? WpfSymbolRegular.ChevronUp16
            : WpfSymbolRegular.ChevronDown16;
        AutomationProperties.SetHelpText(
            TaskShelfHeaderButton, _taskShelfExpanded ? "可用任务已展开" : "可用任务已收起");
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            var project = _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
            if (!project.AddTask(task.Name)) {
                return;
            }
            _expandedTaskName = task.Name;
            _pendingStartAttempt = null;
            RenderTaskPlan();
            _logger.Info($"已添加执行计划任务：{task.Name}。 ");
            UpdateCommandAvailability();
        } catch (Exception exception) {
            HandleProjectEditError("添加执行计划任务失败", exception);
        }
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
        if (sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            var project = _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
            if (!project.RemoveTask(task.Name)) {
                return;
            }
            if (_expandedTaskName == task.Name) {
                _expandedTaskName = null;
            }
            CloseTaskDescriptionDrawer();
            _pendingStartAttempt = null;
            RenderTaskPlan();
            _logger.Info($"已从执行计划移除任务：{task.Name}。 ");
            UpdateCommandAvailability();
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
    }

    private void OptionInputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox editor) {
            _ = CommitOptionInput(editor);
        }
    }

    private void OptionCaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOptionEditors || !CanEditConfiguration
            || sender is not WpfComboBox {
                Tag: OptionCaseTag tag,
                SelectedItem: ProjectCaseEditor selected
            }) {
            return;
        }

        try {
            _ = (_projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"))
                .SetSelectedCase(tag.ConfigurationId, tag.OptionName, selected.Name);
            _pendingStartAttempt = null;
            RenderTaskPlan();
            _logger.Info($"已保存 MaaNOP explicit case：option={tag.OptionName}。 ");
            UpdateCommandAvailability();
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
        var project = _projectPlan
                      ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
        _updatingOptionEditors = true;
        try {
            RenderConfigurationTabs();
            RenderAvailableTaskShelf(project);
            RenderPlanItems(project);
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
            UpdatePlanSummary(project);
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private void RenderAvailableTaskShelf(ProjectPlanModule project)
    {
        AvailableTaskCountText.Text = project.Tasks.Count.ToString();
        AvailableTasksPanel.Children.Clear();
        foreach (var task in project.Tasks) {
            var label = new TextBlock { Text = task.Label, VerticalAlignment = VerticalAlignment.Center };
            var icon = CreateSymbolIcon(WpfSymbolRegular.Add16);
            icon.Margin = new Thickness(8, 0, 0, 0);
            icon.Foreground = (WpfBrush)FindResource("Brush.TasksAccent");
            var content = new StackPanel { Orientation = WpfOrientation.Horizontal };
            content.Children.Add(label);
            content.Children.Add(icon);
            var button = new WpfButton {
                Content = content,
                Tag = task,
                IsEnabled = !project.SelectedTaskNames.Contains(task.Name, StringComparer.Ordinal),
                Style = (Style)FindResource("TaskChipButtonStyle")
            };
            AutomationProperties.SetName(button, $"添加任务：{task.Label}");
            button.Click += AddTaskButton_Click;
            AvailableTasksPanel.Children.Add(button);
        }
    }

    private void RenderPlanItems(ProjectPlanModule project)
    {
        PlanItemsPanel.Children.Clear();
        _dropIndicators.Clear();
        _planItemContainers.Clear();
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

    private void OpenTaskDescriptionLink(Uri uri)
    {
        try {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        } catch (Exception exception) {
            _logger.Warn("打开任务说明链接失败。", exception);
            ShowActionableError("无法打开链接", exception, "请检查默认浏览器设置。", offerLogDirectory: false);
        }
    }

    private Border CreatePlanItem(ProjectTaskChoice task, bool expanded)
    {
        ProjectConfigurationView configuration;
        try {
            configuration = (_projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"))
                .GetConfiguration(task.Name);
        } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) {
            configuration = new ProjectConfigurationView([], []);
        }
        var hasParameters = EnumerateOptions(configuration)
            .Any(option => option.Kind != ProjectOptionKind.Input || option.Inputs.Count != 0);
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

        var summary = expanded ? string.Empty : CreateParameterSummary(configuration);
        layout.Children.Add(CreatePlanItemHeader(task, expanded, hasParameters, summary));

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

    private Grid CreatePlanItemHeader(ProjectTaskChoice task, bool expanded, bool hasParameters, string summary)
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
        dragHandle.PreviewKeyDown += PlanDragHandle_PreviewKeyDown;
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
        if (summary.Length != 0) {
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
                Text = summary,
                FontWeight = FontWeights.Normal,
                Foreground = (WpfBrush)FindResource("Brush.Text.Secondary"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(summaryText, 2);
            title.Children.Add(summaryText);
        }
        var main = new WpfButton {
            Tag = task,
            Content = summary.Length == 0 ? label : title,
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
            Grid.SetColumn(information, 2);
            header.Children.Add(information);
        }

        var remove = CreateIconButton(WpfSymbolRegular.Dismiss16, $"从执行计划移除 {task.Label}");
        remove.Tag = task;
        remove.Click += RemovePlanItemButton_Click;
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);
        return header;
    }

    private static string CreateParameterSummary(ProjectConfigurationView configuration)
    {
        var parameters = new List<string>();
        foreach (var option in EnumerateOptions(configuration)) {
            var label = string.IsNullOrWhiteSpace(option.Label) ? option.Name : option.Label;
            if (option.Kind == ProjectOptionKind.Input) {
                foreach (var input in option.Inputs) {
                    var inputLabel = string.IsNullOrWhiteSpace(input.Label) ? label : input.Label;
                    parameters.Add($"{inputLabel}：{input.Value}");
                }
            } else {
                var selected = option.Cases.Single(item => item.Name == option.SelectedCase);
                parameters.Add($"{label}：{selected.Label}");
            }
        }
        return string.Join(" · ", parameters);
    }

    private FrameworkElement CreateParameterEditor(ProjectConfigurationView configuration)
    {
        var panel = new ResponsiveWrapPanel();
        foreach (var option in EnumerateOptions(configuration)) {
            if (option.Kind == ProjectOptionKind.Input) {
                foreach (var input in option.Inputs) {
                    panel.Children.Add(CreateInputEditor(option, input));
                }
            } else {
                panel.Children.Add(CreateCaseEditor(option));
            }
        }

        return panel;
    }

    private Border CreateInputEditor(ProjectOptionEditor option, ProjectInputEditor input)
    {
        var label = string.IsNullOrWhiteSpace(input.Label)
            ? string.IsNullOrWhiteSpace(option.Label) ? option.Name : option.Label
            : input.Label;
        var description = string.IsNullOrWhiteSpace(input.Description) ? option.Description : input.Description;
        var content = new StackPanel();
        content.Children.Add(CreateParameterLabel(label, description));
        var editor = new WpfTextBox {
            Margin = new Thickness(0, 5, 0, 0),
            Text = input.Value,
            Tag = new OptionInputTag(_projectPlan!.ActiveConfigurationId, option.Name, input.Name, input.Value),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ToolTip = input.PatternMessage
        };
        AutomationProperties.SetName(editor, label);
        if (!string.IsNullOrWhiteSpace(description)) {
            AutomationProperties.SetHelpText(editor, description);
        }
        editor.LostKeyboardFocus += OptionInputTextBox_LostKeyboardFocus;
        content.Children.Add(editor);
        return new Border { Style = (Style)FindResource("OptionTileStyle"), Child = content };
    }

    private Border CreateCaseEditor(ProjectOptionEditor option)
    {
        var label = string.IsNullOrWhiteSpace(option.Label) ? option.Name : option.Label;
        var content = new StackPanel();
        content.Children.Add(CreateParameterLabel(label, option.Description));
        var selector = new WpfComboBox {
            Margin = new Thickness(0, 5, 0, 0),
            ItemsSource = option.Cases,
            DisplayMemberPath = nameof(ProjectCaseEditor.Label),
            SelectedItem = option.Cases.Single(item => item.Name == option.SelectedCase),
            Tag = new OptionCaseTag(_projectPlan!.ActiveConfigurationId, option.Name),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ToolTip = option.IsExplicit
                ? "当前值由用户显式设置"
                : $"当前跟随项目默认：{option.DefaultCase}"
        };
        AutomationProperties.SetName(selector, label);
        if (!string.IsNullOrWhiteSpace(option.Description)) {
            AutomationProperties.SetHelpText(selector, option.Description);
        }
        selector.SelectionChanged += OptionCaseComboBox_SelectionChanged;
        content.Children.Add(selector);
        return new Border { Style = (Style)FindResource("OptionTileStyle"), Child = content };
    }

    private Grid CreateParameterLabel(string label, string? description)
    {
        var header = new Grid();
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

    private void PlanDragHandle_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0
            || sender is not WpfButton { Tag: ProjectTaskChoice task }
            || e.Key is not (Key.Up or Key.Down)) {
            return;
        }
        var project = _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
        var currentIndex = project.SelectedTaskNames.ToList().IndexOf(task.Name);
        var targetIndex = e.Key == Key.Up ? currentIndex - 1 : currentIndex + 1;
        if (targetIndex < 0 || targetIndex >= project.SelectedTaskNames.Count) {
            return;
        }
        try {
            _expandedTaskName = null;
            _ = project.MoveTask(task.Name, targetIndex);
            _pendingStartAttempt = null;
            RenderTaskPlan();
            UpdateCommandAvailability();
            e.Handled = true;
        } catch (Exception exception) {
            HandleProjectEditError("调整执行计划顺序失败", exception);
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
        if (e.Data.GetData(PlanItemDragDataFormat) is not string taskName) {
            return;
        }
        try {
            var project = _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
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
            UpdateCommandAvailability();
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

    private void UpdatePlanSummary(ProjectPlanModule project) => UpdateCommandAvailability();

    private void HandleProjectEditError(string operation, Exception exception)
    {
        HandleOperationError(operation, exception);
        ShowProjectValidationError(exception);
        TryRenderTaskPlan();
    }

    private void ShowProjectValidationError(Exception exception)
    {
        ProjectValidationText.Text = exception.GetBaseException().Message;
        ProjectValidationBorder.Visibility = Visibility.Visible;
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
        if (operation.Contains("启动", StringComparison.Ordinal)) {
            return "请检查桌面分身连接状态后重试；若微端启动器缺失，请先通过 QQ 游戏平台安装火影忍者 Online。";
        }

        if (operation.Contains("桌面分身", StringComparison.Ordinal)
            || operation.Contains("子桌面", StringComparison.Ordinal)) {
            return "请确认程序以管理员权限运行，并检查桌面分身状态后重试。";
        }

        return "请检查当前配置和系统状态后重试。";
    }

    private void LoadProject()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config", "maanop-config.json");
        var project = ProjectPlanModule.Open(AppContext.BaseDirectory, configPath);
        _projectPlan = project;
        _pendingStartAttempt = null;
        _expandedTaskName = null;
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
        UpdateCommandAvailability();
    }

    private void ShowProjectUnavailableState(
        string emptyStateTitle,
        string emptyStateDetail)
    {
        _projectPlan = null;
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

    internal static void WriteWorkerDiagnosticLog(AppLogger logger, WorkerLogEntry entry)
    {
        var message = $"Worker #{entry.Sequence} [{entry.Source}] {entry.Message}";
        switch (ParseWorkerLogLevel(entry.Level)) {
            case LogLevel.Critical:
                logger.Critical(message);
                break;
            case LogLevel.Error:
                logger.Error(message);
                break;
            case LogLevel.Warn:
                logger.Warn(message);
                break;
            case LogLevel.Debug:
                logger.Debug(message);
                break;
            default:
                logger.Info(message);
                break;
        }
    }

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
        UpdateWorkerPresentation(snapshot);
        UpdateCommandAvailability();
        UpdatePreviewPolling();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdatePreviewPolling();

    private void MainWindow_StateChanged(object? sender, EventArgs e) => UpdatePreviewPolling();

    private void UpdatePreviewPolling()
    {
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
                        if (_previewPollingGeneration != generation || cancellation.IsCancellationRequested
                            || !TryGetPreviewTarget(out var current) || current != workerId) {
                            return;
                        }
                        if (frame is { State: PreviewState.Streaming, Revision: > 0 }) {
                            DisplayPreviewFrame(frame, pixels);
                        } else {
                            ShowPreviewPlaceholder(frame?.State == PreviewState.WaitingForWindow
                                ? "等待游戏窗口" : "等待游戏画面");
                        }
                    }, cancellation.Token);
            } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            } finally {
                cancellation.Dispose();
            }
        });
    }

    private bool TryGetPreviewTarget(out Guid workerId)
    {
        var worker = _workerSnapshot.WorkerSnapshot;
        var preparing = _busy && _operationStatus.StartsWith("正在准备运行环境", StringComparison.Ordinal);
        if (!_exitInProgress && !_environmentPreparationFailed && !preparing && IsVisible
            && WindowState != WindowState.Minimized && HomeView.Visibility == Visibility.Visible
            && (PreviewCardContent.Visibility == Visibility.Visible || PreviewOverlay.Visibility == Visibility.Visible)
            && _sessionSnapshot.State is ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden
            && _workerSnapshot.Observation == WorkerObservation.Connected && _workerSnapshot.SnapshotFresh
            && worker is { WorkerState: WorkerState.Ready }
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
    {
        var expanded = PreviewCardContent.Visibility != Visibility.Visible;
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

    private void UpdateWorkerPresentation(WorkerCoordinatorSnapshot snapshot)
    {
        UpdateCommandAvailability();
    }

    private void LogListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scrollViewer && ReferenceEquals(sender, HomeLogListBox)) {
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
        return IsScrollViewerNearTop(_homeLogScrollViewer);
    }

    private static bool IsScrollViewerNearTop(ScrollViewer? scrollViewer) =>
        scrollViewer is null || scrollViewer.VerticalOffset <= 0.1;

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

    // Keep the stale snapshot for diagnostics, but a confirmed ended runtime cannot own current controls.
    private WorkerSnapshot? RuntimeControlWorker =>
        _workerSnapshot.Observation is WorkerObservation.ChildSessionEnded or WorkerObservation.WorkerExited
            ? null : _workerSnapshot.WorkerSnapshot;

    private PrimaryActionState DerivePrimaryAction()
    {
        var state = _sessionSnapshot.State;
        var canStartCommand = !_busy && !_exitInProgress
            && state is not ChildSessionState.Connecting && state is not ChildSessionState.Disconnecting;
        var projectReady = _projectPlan is not null;
        var taskCount = _projectPlan?.SelectedTaskNames.Count ?? 0;

        if (!projectReady || taskCount == 0) {
            return new PrimaryActionState(PrimaryActionMode.ConfigureTasks, canStartCommand);
        }

        var worker = RuntimeControlWorker;
        var workerIdleFresh = _workerSnapshot.Observation == WorkerObservation.Connected
            && _workerSnapshot.SnapshotFresh
            && worker is not null && worker.ActiveRun is null && worker.RunState == RunState.Idle;
        var selectedTaskValid = _projectConfigurationValid;
        var environmentReady = workerIdleFresh && worker!.WorkerState == WorkerState.Ready && projectReady
            && selectedTaskValid && worker.RuntimeProfileDigest == _projectPlan!.RuntimeProfileDigest;
        var active = worker?.ActiveRun;
        var hasActiveRun = active is not null;
        var runningRun = active?.State == RunState.Running
            && active.Items.Any(item => item.State is PlanItemState.Starting or PlanItemState.Running);
        var readyToStart = !hasActiveRun && environmentReady
            && state is (ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden);

        if (runningRun) {
            var canStop = canStartCommand && _workerSnapshot.Observation == WorkerObservation.Connected
                && _workerSnapshot.SnapshotFresh;
            return new PrimaryActionState(PrimaryActionMode.Stop, canStop);
        }
        if (hasActiveRun) {
            return new PrimaryActionState(PrimaryActionMode.Transition, false);
        }
        if (readyToStart) {
            return new PrimaryActionState(PrimaryActionMode.Start, canStartCommand);
        }
        var canPrepare = canStartCommand && projectReady && !environmentReady;
        return new PrimaryActionState(PrimaryActionMode.Prepare, canPrepare);
    }

    private void UpdateCommandAvailability()
    {
        var state = _sessionSnapshot.State;
        var canStartCommand = !_busy && !_exitInProgress
            && state is not ChildSessionState.Connecting && state is not ChildSessionState.Disconnecting;

        var sessionConnected = state is (ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden);
        var hasSession = _sessionSnapshot.ChildSessionId is not null;

        HomeDesktopVisibilityButton.Tag = state == ChildSessionState.ConnectedVisible ? "True" : "False";
        if (sessionConnected) {
            HomeDesktopVisibilityText.Text = state == ChildSessionState.ConnectedVisible
                ? "隐藏分身"
                : "显示分身";
            HomeDesktopVisibilityButton.IsEnabled = canStartCommand;
        } else {
            HomeDesktopVisibilityText.Text = "显示分身";
            HomeDesktopVisibilityButton.IsEnabled = false;
        }

        HomeDesktopVisibilityButton.ToolTip = HomeDesktopVisibilityText.Text;
        AutomationProperties.SetName(HomeDesktopVisibilityButton, HomeDesktopVisibilityText.Text);
        HomeTerminateSessionMenuItem.IsEnabled = canStartCommand && hasSession;

        HomeSessionHintText.Visibility = hasSession ? Visibility.Collapsed : Visibility.Visible;

        if (hasSession && sessionConnected) {
            HomeSessionStatusPanel.Visibility = Visibility.Visible;
            HomeSessionStatusText.Text = $"Session {_sessionSnapshot.ChildSessionId}";
            HomeSessionStatusIndicator.Fill = (WpfBrush)FindResource("Brush.Success");
        } else if (_sessionSnapshot.State is ChildSessionState.Connecting or ChildSessionState.Existing
            or ChildSessionState.Disconnecting or ChildSessionState.Faulted) {
            HomeSessionStatusPanel.Visibility = Visibility.Visible;
            HomeSessionStatusText.Text = GetStateBadgeText(_sessionSnapshot.State);
            HomeSessionStatusIndicator.Fill = (WpfBrush)FindResource(GetSessionStatusBrushKey(_sessionSnapshot.State));
        } else {
            HomeSessionStatusPanel.Visibility = Visibility.Collapsed;
        }

        var projectReady = _projectPlan is not null;
        var worker = RuntimeControlWorker;
        UpdateRuntimeHeader(canStartCommand, projectReady, sessionConnected);
        TaskWorkspacePanel.IsEnabled = CanEditConfiguration;
        ConfigurationTabs.IsEnabled = CanEditConfiguration;
        NewConfigurationButton.IsEnabled = CanEditConfiguration;

        var active = worker?.ActiveRun;
        var isRunning = active?.State is RunState.Starting or RunState.Running;
        if (isRunning) {
            if (!_elapsedTimer.IsEnabled) {
                _elapsedTimer.Start();
            }
        } else {
            if (_elapsedTimer.IsEnabled) {
                _elapsedTimer.Stop();
            }
        }

        UpdateRunContextPresentation();
    }

    private void UpdateRuntimeHeader(bool canStartCommand, bool projectReady, bool sessionConnected)
    {
        var worker = RuntimeControlWorker;
        var active = worker?.ActiveRun;
        var primary = DerivePrimaryAction();
        var preparing = _busy && _operationStatus.StartsWith("正在准备运行环境", StringComparison.Ordinal)
            || _sessionSnapshot.State is ChildSessionState.Connecting or ChildSessionState.Existing
            || _workerSnapshot.Observation == WorkerObservation.WorkerStarting
            || worker?.WorkerState == WorkerState.Starting;
        var runtimeFaulted = _environmentPreparationFailed || _sessionSnapshot.State == ChildSessionState.Faulted
            || _workerSnapshot.Observation is WorkerObservation.IpcDisconnected or WorkerObservation.WorkerExited
                or WorkerObservation.WorkerRecoveryConflict
            || worker?.WorkerState == WorkerState.Faulted;
        var runFaulted = active is null && worker?.LastRun?.State == RunState.Failed;
        var ready = sessionConnected && projectReady && _workerSnapshot.Observation == WorkerObservation.Connected
            && _workerSnapshot.SnapshotFresh && worker?.WorkerState == WorkerState.Ready
            && worker.RuntimeProfileDigest == _projectPlan!.RuntimeProfileDigest;
        var running = active?.State is RunState.Starting or RunState.Running or RunState.Stopping;
        var faulted = !running && (runtimeFaulted || runFaulted);
        var starting = active?.State == RunState.Starting
            || _busy && _operationStatus.StartsWith("正在开始任务", StringComparison.Ordinal);
        var stopping = active?.State == RunState.Stopping
            || _busy && _operationStatus.StartsWith("正在停止任务", StringComparison.Ordinal);
        var transitioning = preparing || starting || stopping;

        PrepareEnvironmentButton.Visibility = !transitioning && !running && !faulted && !ready
            ? Visibility.Visible : Visibility.Collapsed;
        PrepareEnvironmentButton.IsEnabled = canStartCommand && projectReady;
        RetryEnvironmentButton.Visibility = !transitioning && faulted ? Visibility.Visible : Visibility.Collapsed;
        RetryEnvironmentButton.IsEnabled = canStartCommand && projectReady;
        StartTaskHeaderButton.Visibility = !transitioning && !running && ready && !faulted
            ? Visibility.Visible : Visibility.Collapsed;
        StartTaskHeaderButton.IsEnabled = primary is { Mode: PrimaryActionMode.Start, CanExecute: true };
        StopTaskHeaderButton.Visibility = !transitioning && active?.State == RunState.Running
            ? Visibility.Visible : Visibility.Collapsed;
        StopTaskHeaderButton.IsEnabled = primary is { Mode: PrimaryActionMode.Stop, CanExecute: true };
        RuntimeHeaderProgressRing.Visibility = transitioning ? Visibility.Visible : Visibility.Collapsed;
        var progressText = stopping ? "正在停止任务" : starting ? "正在开始任务" : "正在准备运行环境";
        RuntimeHeaderProgressRing.ToolTip = progressText;
        System.Windows.Automation.AutomationProperties.SetName(RuntimeHeaderProgressRing, progressText);
    }

    private void ElapsedTimer_Tick(object? sender, EventArgs e) => UpdateRunContextPresentation();

    private void UpdateRunContextPresentation()
    {
        var primary = DerivePrimaryAction();
        var taskCount = _projectPlan?.SelectedTaskNames.Count ?? 0;
        var projectReady = _projectPlan is not null;
        var worker = RuntimeControlWorker;
        var activeRun = worker?.ActiveRun;

        if (_busy) {
            HomeRunContextSubText.Text = "当前操作正在进行，请稍候。";
            return;
        }

        switch (primary.Mode) {
            case PrimaryActionMode.ConfigureTasks:
                HomeRunContextTitleText.Text = "执行计划为空";
                HomeRunContextSubText.Text = !projectReady
                    ? "MaaNOP 项目尚未加载，请确认安装目录包含 interface.json。"
                    : "请先配置需要执行的任务";
                break;

            case PrimaryActionMode.Prepare:
                HomeRunContextTitleText.Text = $"执行计划已配置 · {taskCount} 个任务";
                HomeRunContextSubText.Text = "请先准备运行环境。";
                break;

            case PrimaryActionMode.Start:
                HomeRunContextTitleText.Text = "运行环境已就绪";
                HomeRunContextSubText.Text = "请登录游戏后开始任务";
                break;

            case PrimaryActionMode.Stop:
                if (activeRun is not null) {
                    var (curIdx, total, label) = GetCurrentRunProgress(activeRun, _projectPlan);
                    var elapsed = GetRunElapsedTime(activeRun);
                    HomeRunContextTitleText.Text = $"正在执行 {curIdx} / {total}";
                    HomeRunContextSubText.Text = $"{label} · 已运行 {FormatElapsedTime(elapsed)}";
                } else {
                    HomeRunContextTitleText.Text = "任务正在运行";
                    HomeRunContextSubText.Text = "可随时停止任务。";
                }
                break;

            case PrimaryActionMode.Transition:
                if (activeRun?.State == RunState.Starting) {
                    HomeRunContextTitleText.Text = "正在启动任务…";
                    HomeRunContextSubText.Text = "正在初始化运行环境与 Python 代理。";
                } else if (activeRun?.State == RunState.Stopping) {
                    HomeRunContextTitleText.Text = "正在停止任务…";
                    HomeRunContextSubText.Text = "正在等待 Worker 清理与确认。";
                } else {
                    HomeRunContextTitleText.Text = "任务状态切换中";
                    HomeRunContextSubText.Text = "请稍候。";
                }
                break;
        }
    }

    internal static (int CurrentIndex, int TotalCount, string TaskLabel) GetCurrentRunProgress(
        RunSnapshot run, ProjectPlanModule? projectPlan)
    {
        var total = run.Items.Count > 0 ? run.Items.Count : (projectPlan?.SelectedTaskNames.Count ?? 1);
        var item = GetCurrentPlanItem(run);
        var currentIndex = 1;
        if (item is not null) {
            var index = run.Items.ToList().IndexOf(item);
            if (index >= 0) {
                currentIndex = index + 1;
            } else if (run.CurrentPlanItemIndex is int idx && idx >= 0) {
                currentIndex = idx + 1;
            }
        } else if (run.CurrentPlanItemIndex is int idx && idx >= 0) {
            currentIndex = idx + 1;
        }

        currentIndex = Math.Clamp(currentIndex, 1, Math.Max(1, total));
        var label = item?.TaskLabel;
        if (string.IsNullOrWhiteSpace(label)) {
            var taskName = item?.TaskName ?? projectPlan?.SelectedTaskNames.ElementAtOrDefault(currentIndex - 1);
            if (taskName is not null) {
                label = projectPlan?.Tasks.FirstOrDefault(t => t.Name == taskName)?.Label ?? taskName;
            }
        }
        label = string.IsNullOrWhiteSpace(label) ? "当前任务" : label;
        return (currentIndex, total, label);
    }

    internal static string FormatElapsedTime(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

    private static TimeSpan GetRunElapsedTime(RunSnapshot? run)
    {
        if (run is null) {
            return TimeSpan.Zero;
        }
        var startedAt = run.StartedAtUtc ?? run.CreatedAtUtc;
        var now = DateTime.UtcNow;
        return now > startedAt ? now - startedAt : TimeSpan.Zero;
    }

    private static PlanItemSnapshot? GetCurrentPlanItem(RunSnapshot run)
    {
        if (run.CurrentPlanItemId is Guid currentId) {
            return run.Items.FirstOrDefault(item => item.PlanItemId == currentId);
        }

        if (run.CurrentPlanItemIndex is int currentIndex
            && currentIndex >= 0 && currentIndex < run.Items.Count) {
            return run.Items[currentIndex];
        }

        return run.Items.Count == 1 ? run.Items[0] : null;
    }

    private static string GetPlanItemStateText(PlanItemState state) => state switch {
        PlanItemState.Pending => "等待执行",
        PlanItemState.Starting => "正在启动",
        PlanItemState.Running => "正在执行",
        PlanItemState.Succeeded => "已完成",
        PlanItemState.Failed => "执行失败",
        PlanItemState.Cancelled => "已停止",
        _ => "状态未知"
    };

    private static string GetSessionStatusBrushKey(ChildSessionState state) => state switch {
        ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden => "Brush.Success",
        ChildSessionState.Connecting or ChildSessionState.Existing => "Brush.Primary",
        ChildSessionState.Disconnecting => "Brush.Warning",
        ChildSessionState.Faulted => "Brush.Error",
        _ => "Brush.Text.Muted"
    };

    private static string GetStateBadgeText(ChildSessionState state) => state switch {
        ChildSessionState.NotRunning => "未运行",
        ChildSessionState.Existing => "已检测",
        ChildSessionState.Connecting => "连接中",
        ChildSessionState.ConnectedVisible => "可见",
        ChildSessionState.ConnectedHidden => "已隐藏",
        ChildSessionState.Disconnecting => "正在结束",
        ChildSessionState.Faulted => "连接失败",
        _ => "未知状态"
    };

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
