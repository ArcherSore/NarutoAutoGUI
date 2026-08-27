using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using WpfTextBox = System.Windows.Controls.TextBox;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WpfNavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;

namespace NarutoAutoGUI.Views;

public partial class MainWindow : FluentWindow
{
    private const int MaximumGuiLogEntries = 1000;
    private static readonly TimeSpan PreviewPollingInterval = TimeSpan.FromMilliseconds(
        ProtocolConstants.PreviewIntervalMilliseconds);
    private static readonly TimeSpan PreviewFailureLogInterval = TimeSpan.FromSeconds(30);
    private static readonly Regex DescriptionLineBreakRegex = new(
        @"<br\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DescriptionTagRegex = new(
        @"</?[a-zA-Z][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private enum MainSection
    {
        Home,
        Tasks,
        Settings
    }

    private enum PrimaryActionMode
    {
        Prepare,
        Start,
        Stop,
        Transition
    }

    private sealed record PrimaryActionState(PrimaryActionMode Mode, bool CanExecute);
    private readonly AppLogger _logger;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
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
    private Guid? _previewRunId;
    private long _previewRevision;
    private int _previewPollingGeneration;
    private DateTime _nextPreviewFailureLogAtUtc = DateTime.MinValue;
    private bool _allowClose;
    private bool _busy;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _projectConfigurationValid;
    private bool _updatingTaskSelection;
    private bool _updatingOptionEditors;
    private int _newLogCount;

    private sealed record OptionInputTag(string OptionName, string InputName);

    private sealed record OptionCaseTag(string OptionName);

    internal MainWindow(
        AppLogger logger, AppSettingsStore settingsStore, AppSettings settings,
        ChildSessionManager sessionManager, ChildSessionProgramService programService,
        WorkerCoordinator workerCoordinator, Func<Func<Task>, Task> runApplicationOperationAsync,
        Func<Task> requestExitAsync)
    {
        InitializeComponent();
        DataContext = this;
        _logger = logger;
        _settingsStore = settingsStore;
        _settings = settings;
        _sessionManager = sessionManager;
        _programService = programService;
        _workerCoordinator = workerCoordinator;
        _runApplicationOperationAsync = runApplicationOperationAsync;
        _requestExitAsync = requestExitAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        GamePathTextBox.Text = settings.GameExecutablePath;
        GameArgumentsTextBox.Text = settings.GameArguments;
        MaaNopProjectDirectoryTextBox.Text = settings.MaaNopProjectDirectory;
        HomeLogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _sessionManager.StateChanged += OnSessionStateChanged;
        _workerCoordinator.StateChanged += OnWorkerStateChanged;
        _workerCoordinator.LogReceived += OnWorkerLogReceived;
        _workerSnapshot = workerCoordinator.Snapshot;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
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
        UpdateCommandAvailability();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _homeLogScrollViewer = FindVisualChild<ScrollViewer>(HomeLogListBox);
        TryLoadProject(showError: !string.IsNullOrWhiteSpace(MaaNopProjectDirectoryTextBox.Text));
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
        if (_allowClose) {
            StopPreviewPolling(clearImage: true);
            _sessionManager.StateChanged -= OnSessionStateChanged;
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
            _workerCoordinator.LogReceived -= OnWorkerLogReceived;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            StateChanged -= MainWindow_StateChanged;
            return;
        }

        e.Cancel = true;
        TrySaveSettings(showError: false);
        Hide();
        _logger.Info("主窗口已隐藏到托盘。");
        HiddenToTray?.Invoke(this, EventArgs.Empty);
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
        HomeView.Visibility = section == MainSection.Home
            ? Visibility.Visible
            : Visibility.Collapsed;
        TasksView.Visibility = section == MainSection.Tasks
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsView.Visibility = section == MainSection.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;

        HomeNavigationItem.IsActive = section == MainSection.Home;
        TasksNavigationItem.IsActive = section == MainSection.Tasks;
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
            OperationStatusText.Text = "子桌面已隐藏，连接保持存活";
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

    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseExecutable(GamePathTextBox.Text, "选择游戏程序");
        if (path is not null) {
            GamePathTextBox.Text = path;
            TrySaveSettings(showError: true);
        }
    }

    private void BrowseMaaNopProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseProjectDirectory(
            MaaNopProjectDirectoryTextBox.Text,
            "选择直接包含 interface.json 的 MaaNOP Project Directory");
        if (path is not null) {
            MaaNopProjectDirectoryTextBox.Text = path;
            if (TrySaveSettings(showError: true)) {
                TryLoadProject(showError: true);
            }
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        => TryOpenLogsDirectory(showError: true);

    private bool TryOpenLogsDirectory(bool showError)
    {
        try {
            Directory.CreateDirectory(_logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"\"{_logger.LogDirectory}\"",
                UseShellExecute = true
            });
            return true;
        } catch (Exception exception) {
            _logger.Error("打开日志目录失败。", exception);
            OperationStatusText.Text = "失败：无法打开日志目录";
            if (showError) {
                ShowActionableError(
                    "打开日志目录失败",
                    exception,
                    "请确认 Windows 资源管理器可用，并检查日志目录访问权限后重试。",
                    offerLogDirectory: false);
            }

            return false;
        }
    }

    private void HomePrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        var primary = DerivePrimaryAction();
        if (!primary.CanExecute) {
            return;
        }
        switch (primary.Mode) {
            case PrimaryActionMode.Stop:
                StopRunButton_Click(sender, e);
                break;
            case PrimaryActionMode.Start:
                StartRunButton_Click(sender, e);
                break;
            default:
                PrepareEnvironmentButton_Click(sender, e);
                break;
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

    private async void PrepareEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            "正在准备运行环境...",
            async () =>
            {
                SaveSettings();
                LoadProject();
                var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                await _workerCoordinator.PrepareWorkerAsync(
                    sessionId, _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"));
                await _programService.LaunchIfNeededAsync(sessionId, GamePathTextBox.Text, GameArgumentsTextBox.Text);
                _sessionManager.ShowPreview();
                _logger.Info("真实 E2E 环境已准备；完成游戏登录后即可开始任务。 ");
            });
    }

    private async void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
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
        StopPreviewPolling(clearImage: true);
        await RunOperationAsync(
            "正在停止任务...",
            async () =>
            {
                var activeRun = _workerSnapshot.WorkerSnapshot?.ActiveRun
                                ?? throw new InvalidOperationException("Worker 当前没有 active Run。 ");
                if (activeRun.State != RunState.Running
                    || activeRun.Items.Single().State != PlanItemState.Running) {
                    throw new InvalidOperationException(
                        "首片取消验收必须先确认 Run 与唯一 Plan Item 都已进入 Running。 ");
                }
                var response = await _workerCoordinator.StopRunAsync(activeRun.RunId);
                if (response.Disposition != "stop_requested") {
                    throw new InvalidDataException(
                        $"首次 run.stop 未返回 stop_requested：{response.Disposition}。 ");
                }
                _logger.Info($"run.stop 已确认 stop_requested：runId={activeRun.RunId:D}。 ");
                OperationStatusText.Text = "停止请求已接受，正在等待 MaaFramework Stop 与清理确认";
            });
        UpdatePreviewPolling();
    }

    private void TaskListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTaskSelection || TaskListBox.SelectedItem is not ProjectTaskChoice task) {
            return;
        }
        try {
            (_projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"))
                .SelectTask(task.Name);
            _pendingStartAttempt = null;
            RenderOptionEditors();
            UpdateTaskDescription();
            _logger.Info($"已保存 MaaNOP Config：SelectedTasks=[{task.Name}]。 ");
            UpdateCommandAvailability();
        } catch (Exception exception) {
            RestoreTaskSelection();
            HandleOperationError("保存 MaaNOP task 选择失败", exception);
            ShowProjectValidationError(exception);
        }
    }

    private void OptionInputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_updatingOptionEditors
            || sender is not WpfTextBox { Tag: OptionInputTag tag } textBox) {
            return;
        }

        try {
            var configuration = (_projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"))
                .SetInputValue(tag.OptionName, tag.InputName, textBox.Text);
            _pendingStartAttempt = null;
            RenderOptionEditors(configuration);
            _logger.Info(
                $"已保存 MaaNOP explicit input：option={tag.OptionName}，input={tag.InputName}。 ");
            UpdateCommandAvailability();
        } catch (Exception exception) {
            HandleOperationError("保存 MaaNOP input option 失败", exception);
            TryRenderOptionEditors();
            ShowProjectValidationError(exception);
        }
    }

    private void OptionCaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOptionEditors
            || sender is not WpfComboBox {
                Tag: OptionCaseTag tag,
                SelectedItem: ProjectCaseEditor selected
            }) {
            return;
        }

        try {
            var configuration = (_projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"))
                .SetSelectedCase(tag.OptionName, selected.Name);
            _pendingStartAttempt = null;
            RenderOptionEditors(configuration);
            _logger.Info($"已保存 MaaNOP explicit case：option={tag.OptionName}。 ");
            UpdateCommandAvailability();
        } catch (Exception exception) {
            HandleOperationError("保存 MaaNOP select/switch option 失败", exception);
            TryRenderOptionEditors();
            ShowProjectValidationError(exception);
        }
    }

    private void TryRenderOptionEditors()
    {
        try {
            RenderOptionEditors();
        } catch (Exception exception) {
            _projectConfigurationValid = false;
            ShowProjectValidationError(exception);
            _logger.Warn("刷新 MaaNOP option 编辑器失败。", exception);
            UpdateCommandAvailability();
        }
    }

    private void RenderOptionEditors(ProjectConfigurationView? configuration = null)
    {
        var project = _projectPlan
                      ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
        configuration ??= project.GetConfiguration();
        _updatingOptionEditors = true;
        try {
            OptionEditorPanel.Children.Clear();
            AddOptionSection("全局参数", configuration.GlobalOptions);
            AddOptionSection("任务参数", configuration.TaskOptions);
            if (OptionEditorPanel.Children.Count == 0) {
                OptionEditorPanel.Children.Add(new TextBlock {
                    Text = project.SelectedTaskName is null
                        ? "请先选择任务。"
                        : "当前任务没有可编辑参数。",
                    Style = (Style)FindResource("MutedTextStyle")
                });
            }

            var allOptions = EnumerateOptions(configuration).ToArray();
            var explicitCount = allOptions.Count(option => option.IsExplicit);
            HomeOptionSummaryText.Text =
                $"{allOptions.Length} 个启用参数 · {explicitCount} 个显式设置";
            ProjectValidationText.Text = string.Empty;
            ProjectValidationBorder.Visibility = Visibility.Collapsed;
            _projectConfigurationValid = true;
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private void RestoreTaskSelection()
    {
        _updatingTaskSelection = true;
        try {
            TaskListBox.SelectedItem = _projectPlan?.SelectedTaskName is null
                ? null
                : _projectPlan.Tasks.Single(task => task.Name == _projectPlan.SelectedTaskName);
        } finally {
            _updatingTaskSelection = false;
        }
        UpdateTaskDescription();
    }

    private void UpdateTaskDescription()
    {
        var markup = (TaskListBox.SelectedItem as ProjectTaskChoice)?.Description;
        TaskDescriptionText.Text = RenderDescriptionText(markup);
        TaskDescriptionPanel.Visibility = string.IsNullOrWhiteSpace(markup)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    internal static string RenderDescriptionText(string? markup)
    {
        if (string.IsNullOrEmpty(markup)) {
            return string.Empty;
        }
        var withBreaks = DescriptionLineBreakRegex.Replace(markup, "\n");
        return DescriptionTagRegex.Replace(withBreaks, string.Empty);
    }

    private void ShowProjectValidationError(Exception exception)
    {
        ProjectValidationText.Text = exception.GetBaseException().Message;
        ProjectValidationBorder.Visibility = Visibility.Visible;
    }

    private void AddOptionSection(string title, IReadOnlyList<ProjectOptionEditor> options)
    {
        if (options.Count == 0) {
            return;
        }
        var sectionTitle = new TextBlock {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, OptionEditorPanel.Children.Count == 0 ? 0 : 12, 0, 8),
            Style = (Style)FindResource("MutedTextStyle")
        };
        OptionEditorPanel.Children.Add(sectionTitle);
        foreach (var option in options) {
            AddOptionEditor(option, depth: 0);
        }
    }

    private void AddOptionEditor(ProjectOptionEditor option, int depth)
    {
        var row = new Border {
            Style = (Style)FindResource("ParameterRowStyle"),
            Margin = new Thickness(Math.Min(depth, 3) * 12, 0, 0, 8)
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });

        var information = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock {
            Text = string.IsNullOrWhiteSpace(option.Label) ? option.Name : option.Label,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = option.Description
        };
        information.Children.Add(title);
        var description = option.Description;
        if (string.IsNullOrWhiteSpace(description) && option.Inputs.Count == 1) {
            description = option.Inputs[0].Description;
        }
        if (!string.IsNullOrWhiteSpace(description)) {
            information.Children.Add(new TextBlock {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Style = (Style)FindResource("SecondaryTextStyle")
            });
        }
        content.Children.Add(information);

        var editorPanel = new StackPanel {
            MaxWidth = 400,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (option.Kind == ProjectOptionKind.Input) {
            if (option.Inputs.Count == 0) {
                editorPanel.Children.Add(new TextBlock {
                    Text = "没有可编辑输入",
                    Style = (Style)FindResource("MutedTextStyle")
                });
            }
            for (var index = 0; index < option.Inputs.Count; index++) {
                var input = option.Inputs[index];
                var inputPanel = new StackPanel {
                    Margin = new Thickness(0, index == 0 ? 0 : 10, 0, 0)
                };
                if (option.Inputs.Count > 1) {
                    inputPanel.Children.Add(new TextBlock {
                        Text = string.IsNullOrWhiteSpace(input.Label) ? input.Name : input.Label,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        ToolTip = input.Description
                    });
                    if (!string.IsNullOrWhiteSpace(input.Description)) {
                        inputPanel.Children.Add(new TextBlock {
                            Text = input.Description,
                            Margin = new Thickness(0, 2, 0, 5),
                            Style = (Style)FindResource("MutedTextStyle")
                        });
                    }
                }
                var editor = new WpfTextBox {
                    Text = input.Value,
                    Tag = new OptionInputTag(option.Name, input.Name),
                    MinWidth = 0,
                    MaxWidth = 400,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    ToolTip = input.PatternMessage ?? input.Description
                };
                editor.LostKeyboardFocus += OptionInputTextBox_LostKeyboardFocus;
                inputPanel.Children.Add(editor);
                editorPanel.Children.Add(inputPanel);
            }
        } else {
            var selector = new WpfComboBox {
                ItemsSource = option.Cases,
                DisplayMemberPath = nameof(ProjectCaseEditor.Label),
                SelectedItem = option.Cases.Single(item => item.Name == option.SelectedCase),
                Tag = new OptionCaseTag(option.Name),
                MinWidth = 0,
                MaxWidth = 400,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                ToolTip = option.IsExplicit
                    ? "当前值由用户显式设置"
                    : $"当前跟随项目默认：{option.DefaultCase}"
            };
            selector.SelectionChanged += OptionCaseComboBox_SelectionChanged;
            editorPanel.Children.Add(selector);
        }

        Grid.SetColumn(editorPanel, 2);
        content.Children.Add(editorPanel);
        row.Child = content;
        OptionEditorPanel.Children.Add(row);
        foreach (var child in option.ActiveChildren) {
            AddOptionEditor(child, depth + 1);
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
            OperationStatusText.Text = "操作完成";
        } catch (OperationCanceledException) {
            _logger.Warn($"操作已取消：{status}");
            OperationStatusText.Text = "操作已取消";
        } catch (Exception exception) {
            var operationName = status.TrimEnd('.', '…');
            if (operationName.StartsWith("正在", StringComparison.Ordinal)) {
                operationName = operationName[2..];
            }

            HandleOperationError($"{operationName}失败", exception);
        } finally {
            SetBusy(false, OperationStatusText.Text);
        }
    }

    private void HandleOperationError(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        OperationStatusText.Text = $"失败：{operation}";
        ShowActionableError(operation, exception, GetRecoveryGuidance(operation), offerLogDirectory: true);
    }

    private void SaveSettings()
    {
        _settings.GameExecutablePath = GamePathTextBox.Text.Trim();
        _settings.GameArguments = GameArgumentsTextBox.Text.Trim();
        _settings.MaaNopProjectDirectory = MaaNopProjectDirectoryTextBox.Text.Trim();
        _settingsStore.Save(_settings);
        MaaNopProjectDirectoryTextBox.Text = _settings.MaaNopProjectDirectory;
    }

    private void PathsTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        TrySaveSettings(showError: true);

    private void MaaNopProjectDirectoryTextBox_LostKeyboardFocus(
        object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (TrySaveSettings(showError: true)) {
            TryLoadProject(showError: true);
        }
    }

    private bool TrySaveSettings(bool showError)
    {
        try {
            SaveSettings();
            return true;
        } catch (Exception exception) {
            _logger.Error("保存程序路径配置失败。", exception);
            OperationStatusText.Text = "失败：保存配置";
            if (showError) {
                ShowActionableError("保存配置失败", exception, "请确认程序目录可写，或将程序移动到有写入权限的目录后重试。", offerLogDirectory: true);
            }

            return false;
        }
    }

    private void LoadProject()
    {
        var projectDirectory = MaaNopProjectDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(projectDirectory)) {
            throw new InvalidOperationException("请选择 MaaNOP Project Directory。 ");
        }

        var project = ProjectPlanModule.Open(projectDirectory, _settingsStore.MaaNopConfigPath);
        _projectPlan = project;
        _pendingStartAttempt = null;
        TaskListBox.ItemsSource = project.Tasks;
        RestoreTaskSelection();
        ProjectEmptyStatePanel.Visibility = Visibility.Collapsed;
        TaskWorkspacePanel.Visibility = Visibility.Visible;

        RenderOptionEditors(project.GetConfiguration());
        _logger.Info(
            $"已加载 MaaNOP Project Interface：{project.ProjectName} {project.ProjectVersion}；"
            + $"interfaceDigest={project.SourceInterfaceDigest}；"
            + $"runtimeProfileDigest={project.RuntimeProfileDigest}。 ");
        UpdateCommandAvailability();
    }

    private bool TryLoadProject(bool showError)
    {
        if (string.IsNullOrWhiteSpace(MaaNopProjectDirectoryTextBox.Text)) {
            ShowProjectUnavailableState(
                "配置项目后显示参数摘要",
                "尚未配置 MaaNOP 项目",
                "请先在设置中选择直接包含 interface.json 的 MaaNOP 项目目录。");
            ProjectValidationText.Text = string.Empty;
            ProjectValidationBorder.Visibility = Visibility.Collapsed;
            UpdateCommandAvailability();
            return false;
        }

        try {
            LoadProject();
            return true;
        } catch (Exception exception) {
            ShowProjectUnavailableState(
                "修正项目后显示参数摘要",
                "MaaNOP 项目无法加载",
                "请检查项目目录和 interface.json，然后重试。");
            ShowProjectValidationError(exception);
            _logger.Warn("加载 MaaNOP Project Interface 失败。", exception);
            UpdateCommandAvailability();
            if (showError) {
                ShowActionableError(
                    "加载 MaaNOP 项目失败",
                    exception,
                    "请选择直接包含真实 interface.json、agent 和 resource 的 MaaNOP Project Directory。",
                    offerLogDirectory: true);
            }
            return false;
        }
    }

    private void ShowProjectUnavailableState(
        string optionSummary,
        string emptyStateTitle,
        string emptyStateDetail)
    {
        _projectPlan = null;
        _pendingStartAttempt = null;
        _projectConfigurationValid = false;
        TaskListBox.ItemsSource = null;
        OptionEditorPanel.Children.Clear();
        HomeOptionSummaryText.Text = optionSummary;
        ProjectEmptyStateTitleText.Text = emptyStateTitle;
        ProjectEmptyStateDetailText.Text = emptyStateDetail;
        TaskWorkspacePanel.Visibility = Visibility.Collapsed;
        ProjectEmptyStatePanel.Visibility = Visibility.Visible;
        UpdateTaskDescription();
    }

    private static string? BrowseExecutable(string currentPath, string title)
    {
        var dialog = new WpfOpenFileDialog {
            Title = title,
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(currentPath)) {
            try {
                dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(currentPath));
                dialog.FileName = Path.GetFileName(currentPath);
            } catch {
                // Ignore malformed current text; the dialog remains usable.
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? BrowseProjectDirectory(string currentPath, string title)
    {
        var dialog = new WpfOpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)) {
            dialog.InitialDirectory = Path.GetFullPath(currentPath);
        }
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    internal static bool IsUserFacingRunLog(WorkerLogEntry entry) =>
        string.Equals(entry.Source, ProtocolConstants.MaaNopRunLogSource, StringComparison.Ordinal);

    internal static LogEntry? CreateUserFacingRunLogEntry(WorkerLogEntry workerEntry)
    {
        if (!IsUserFacingRunLog(workerEntry)) {
            return null;
        }

        var timestampUtc = DateTime.SpecifyKind(workerEntry.TimestampUtc, DateTimeKind.Utc);
        return new LogEntry(new DateTimeOffset(timestampUtc).ToLocalTime(), ParseWorkerLogLevel(workerEntry.Level), workerEntry.Message);
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
        var shouldFollow = _followLogs && IsLogNearBottom();
        LogLines.Add(entry);
        while (LogLines.Count > MaximumGuiLogEntries) {
            LogLines.RemoveAt(0);
        }

        if (shouldFollow) {
            _newLogCount = 0;
            UpdateResumeLogFollowButton();
            _ = Dispatcher.BeginInvoke(ScrollLogsToEnd, DispatcherPriority.Background);
            return;
        }

        _followLogs = false;
        _newLogCount++;
        UpdateResumeLogFollowButton();
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
        if (!TryGetPreviewTarget(out var workerInstanceId, out var runId)) {
            StopPreviewPolling(clearImage: true);
            return;
        }
        if (_previewWorkerInstanceId == workerInstanceId && _previewRunId == runId
            && _previewPollingTask is { IsCompleted: false }) {
            return;
        }

        StopPreviewPolling(clearImage: true);
        _previewWorkerInstanceId = workerInstanceId;
        _previewRunId = runId;
        _previewRevision = 0;
        var cancellation = new CancellationTokenSource();
        _previewPollingCancellation = cancellation;
        var generation = _previewPollingGeneration;
        _previewPollingTask = RunPreviewPollingAsync(workerInstanceId, runId, generation, cancellation);
    }

    private async Task RunPreviewPollingAsync(
        Guid workerInstanceId, Guid runId, int generation, CancellationTokenSource cancellation)
    {
        try {
            while (!cancellation.IsCancellationRequested) {
                var cycleStarted = Stopwatch.GetTimestamp();
                try {
                    var response = await _workerCoordinator.GetLatestPreviewAsync(
                        runId, _previewRevision, cancellation.Token);
                    if (!IsCurrentPreviewTarget(workerInstanceId, runId, generation)) {
                        return;
                    }
                    if (response.Disposition == "frame") {
                        _previewRevision = response.Revision;
                        DisplayPreviewFrame(response);
                    }
                } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                    return;
                } catch (Exception exception) {
                    LogPreviewFailure("Preview 请求或显示失败。", exception);
                }

                var remaining = PreviewPollingInterval - Stopwatch.GetElapsedTime(cycleStarted);
                if (remaining > TimeSpan.Zero) {
                    await Task.Delay(remaining, cancellation.Token);
                }
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } finally {
            cancellation.Dispose();
            if (_previewPollingGeneration == generation) {
                _previewPollingCancellation = null;
                _previewPollingTask = null;
            }
        }
    }

    private bool TryGetPreviewTarget(out Guid workerInstanceId, out Guid runId)
    {
        var worker = _workerSnapshot.WorkerSnapshot;
        var activeRun = worker?.ActiveRun;
        if (IsVisible && WindowState != WindowState.Minimized && HomeView.Visibility == Visibility.Visible
            && _workerSnapshot.Observation == WorkerObservation.Connected && _workerSnapshot.SnapshotFresh
            && worker is not null && activeRun?.State is RunState.Starting or RunState.Running) {
            workerInstanceId = worker.WorkerInstanceId;
            runId = activeRun.RunId;
            return true;
        }

        workerInstanceId = Guid.Empty;
        runId = Guid.Empty;
        return false;
    }

    private bool IsCurrentPreviewTarget(Guid workerInstanceId, Guid runId, int generation) =>
        _previewPollingGeneration == generation && _previewWorkerInstanceId == workerInstanceId
        && _previewRunId == runId && TryGetPreviewTarget(out var currentWorkerInstanceId, out var currentRunId)
        && currentWorkerInstanceId == workerInstanceId && currentRunId == runId;

    private void StopPreviewPolling(bool clearImage)
    {
        _previewPollingGeneration++;
        var cancellation = _previewPollingCancellation;
        _previewPollingCancellation = null;
        _previewPollingTask = null;
        _previewWorkerInstanceId = null;
        _previewRunId = null;
        _previewRevision = 0;
        _nextPreviewFailureLogAtUtc = DateTime.MinValue;
        try {
            cancellation?.Cancel();
        } catch (Exception exception) {
            LogPreviewFailure("停止 Preview 轮询失败。", exception);
        }
        if (clearImage) {
            ShowPreviewPlaceholder();
        }
    }

    private void DisplayPreviewFrame(PreviewGetLatestResponse response)
    {
        using var stream = new MemoryStream(response.PngBytes!, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        if (image.PixelWidth != response.PixelWidth || image.PixelHeight != response.PixelHeight) {
            throw new InvalidDataException("Preview PNG 像素尺寸与响应元数据不一致。 ");
        }
        image.Freeze();
        HomePreviewImage.Source = image;
        HomePreviewImage.Visibility = Visibility.Visible;
        HomePreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ShowPreviewPlaceholder()
    {
        HomePreviewImage.Source = null;
        HomePreviewImage.Visibility = Visibility.Collapsed;
        HomePreviewPlaceholder.Visibility = Visibility.Visible;
    }

    private void LogPreviewFailure(string message, Exception exception)
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _nextPreviewFailureLogAtUtc) {
            return;
        }
        _nextPreviewFailureLogAtUtc = nowUtc + PreviewFailureLogInterval;
        try {
            _logger.Warn(message, exception);
        } catch {
            // Preview diagnostics must never affect Run or GUI lifecycle.
        }
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
        HomeWorkerSummaryText.Text = GetHomeWorkerSummary(snapshot);
        var worker = snapshot.WorkerSnapshot;
        if (worker is null) {
            HomeRunSummaryText.Text = "尚未运行";
            return;
        }

        var run = worker.ActiveRun ?? worker.LastRun;
        if (run is null) {
            HomeRunSummaryText.Text = "尚未运行";
            return;
        }
        HomeRunSummaryText.Text = GetHomeRunSummary(run);
    }

    private static string GetHomeWorkerSummary(WorkerCoordinatorSnapshot snapshot)
    {
        if (snapshot.Observation != WorkerObservation.Connected) {
            return snapshot.Observation switch {
                WorkerObservation.WorkerNotStarted => "尚未启动",
                WorkerObservation.WorkerStarting => "正在启动",
                WorkerObservation.IpcDisconnected => "连接已断开",
                WorkerObservation.WorkerExited => "已退出",
                WorkerObservation.WorkerRecoveryConflict => "需要恢复",
                WorkerObservation.ChildSessionEnded => "桌面分身已结束",
                _ => "状态未知"
            };
        }

        if (!snapshot.SnapshotFresh) {
            return "正在同步状态";
        }

        return snapshot.WorkerSnapshot?.WorkerState switch {
            WorkerState.Starting => "正在启动",
            WorkerState.Ready => "已就绪",
            WorkerState.NotReady => "尚未就绪",
            WorkerState.Faulted => "运行异常",
            WorkerState.Stopping => "正在停止",
            _ => "已连接"
        };
    }

    private static string GetHomeRunSummary(RunSnapshot run)
    {
        var taskLabel = run.Items.SingleOrDefault()?.TaskLabel;
        var stateText = run.State switch {
            RunState.Idle => "尚未运行",
            RunState.Starting => "正在启动",
            RunState.Running => "正在运行",
            RunState.Stopping => "正在停止",
            RunState.Succeeded => "已完成",
            RunState.Failed => "运行失败",
            RunState.Cancelled => "已停止",
            _ => "状态未知"
        };
        return string.IsNullOrWhiteSpace(taskLabel)
            ? stateText
            : $"{taskLabel} · {stateText}";
    }

    private void LogListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scrollViewer && ReferenceEquals(sender, HomeLogListBox)) {
            _homeLogScrollViewer = scrollViewer;
        }

        if (e.VerticalChange < 0) {
            _followLogs = false;
            UpdateResumeLogFollowButton();
            return;
        }

        if (e.VerticalChange > 0 && IsScrollViewerNearBottom(e.OriginalSource as ScrollViewer)) {
            ResumeLogFollow(scrollToEnd: false);
        }
    }

    private void ResumeLogFollowButton_Click(object sender, RoutedEventArgs e) =>
        ResumeLogFollow(scrollToEnd: true);

    private void ResumeLogFollow(bool scrollToEnd)
    {
        _followLogs = true;
        _newLogCount = 0;
        UpdateResumeLogFollowButton();
        if (scrollToEnd) {
            ScrollLogsToEnd();
        }
    }

    private bool IsLogNearBottom()
    {
        _homeLogScrollViewer ??= FindVisualChild<ScrollViewer>(HomeLogListBox);
        return IsScrollViewerNearBottom(_homeLogScrollViewer);
    }

    private static bool IsScrollViewerNearBottom(ScrollViewer? scrollViewer) =>
        scrollViewer is null || scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 2.0;

    private void ScrollLogsToEnd()
    {
        if (LogLines.LastOrDefault() is LogEntry lastLine) {
            HomeLogListBox.ScrollIntoView(lastLine);
        }
    }

    private void UpdateResumeLogFollowButton()
    {
        var visibility = _followLogs || _newLogCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        var content = $"{_newLogCount} 条新日志，继续跟随(_F)";
        var automationName = $"{_newLogCount} 条新日志，继续跟随";
        HomeResumeLogFollowButton.Visibility = visibility;
        HomeResumeLogFollowButton.Content = content;
        System.Windows.Automation.AutomationProperties.SetName(HomeResumeLogFollowButton, automationName);
    }

    private void OnSessionStateChanged(object? sender, ChildSessionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, snapshot));
            return;
        }

        _sessionSnapshot = snapshot;
        UpdateCommandAvailability();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        OperationStatusText.Text = status;
        OperationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        UpdateCommandAvailability();
    }

    private PrimaryActionState DerivePrimaryAction()
    {
        var state = _sessionSnapshot.State;
        var canStartCommand = !_busy && !_exitInProgress
            && state is not ChildSessionState.Connecting && state is not ChildSessionState.Disconnecting;
        var projectReady = _projectPlan is not null;
        var worker = _workerSnapshot.WorkerSnapshot;
        var workerIdleFresh = _workerSnapshot.Observation == WorkerObservation.Connected && _workerSnapshot.SnapshotFresh
            && worker is not null && worker.ActiveRun is null && worker.RunState == RunState.Idle;
        var selectedTaskValid = _projectPlan?.SelectedTaskName is not null && _projectConfigurationValid;
        var environmentReady = workerIdleFresh && worker!.WorkerState == WorkerState.Ready && projectReady
            && selectedTaskValid && worker.RuntimeProfileDigest == _projectPlan!.RuntimeProfileDigest;
        var active = worker?.ActiveRun;
        var hasActiveRun = active is not null;
        var runningRun = active?.State == RunState.Running && active.Items.Count == 1
            && active.Items[0].State == PlanItemState.Running;
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
        HomeDesktopVisibilityButton.Visibility = sessionConnected ? Visibility.Visible : Visibility.Collapsed;
        if (sessionConnected) {
            HomeDesktopVisibilityButton.Content = state == ChildSessionState.ConnectedVisible
                ? "隐藏桌面(_H)"
                : "打开完整桌面(_S)";
            HomeDesktopVisibilityButton.IsEnabled = canStartCommand;
        }
        HomeTerminateSessionButton.IsEnabled = canStartCommand && _sessionSnapshot.ChildSessionId is not null;
        HomeTerminateSessionButton.Visibility = _sessionSnapshot.ChildSessionId is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        var projectReady = _projectPlan is not null;
        var worker = _workerSnapshot.WorkerSnapshot;
        var workerIdleFresh = _workerSnapshot.Observation == WorkerObservation.Connected && _workerSnapshot.SnapshotFresh
            && worker is not null && worker.ActiveRun is null && worker.RunState == RunState.Idle;
        var canEditProject = canStartCommand
            && (_workerSnapshot.Observation is WorkerObservation.WorkerNotStarted or WorkerObservation.ChildSessionEnded
                || workerIdleFresh);
        MaaNopProjectDirectoryTextBox.IsEnabled = canEditProject;
        BrowseMaaNopProjectButton.IsEnabled = canEditProject;
        TaskListBox.IsEnabled = canEditProject && projectReady;
        OptionEditorPanel.IsEnabled = canEditProject && projectReady;

        var primary = DerivePrimaryAction();
        HomePrimaryActionButton.Content = primary.Mode switch {
            PrimaryActionMode.Start => "开始任务(_U)",
            PrimaryActionMode.Stop => "停止任务(_X)",
            _ => "准备运行环境(_O)"
        };
        HomePrimaryActionButton.Style = (Style)FindResource(primary.Mode switch {
            PrimaryActionMode.Start => "PrimaryButtonStyle",
            PrimaryActionMode.Stop => "DestructiveButtonStyle",
            _ => "SecondaryButtonStyle"
        });
        HomePrimaryActionButton.IsEnabled = primary.CanExecute;

        var active = worker?.ActiveRun;
        var hasActiveRun = active is not null;
        var runningRun = primary.Mode == PrimaryActionMode.Stop;
        var readyToStart = primary.Mode == PrimaryActionMode.Start;

        HomeNextStepText.Text = _busy
            ? "当前操作正在进行，请稍候。"
            : runningRun
                ? "任务正在运行，可随时停止。"
                : hasActiveRun
                    ? "任务状态正在切换，请稍候。"
                    : !projectReady
                        ? "请先在“设置”中选择 MaaNOP 项目目录。"
                        : readyToStart
                            ? "运行环境已就绪，可以开始任务。"
                            : "准备桌面分身、Worker 和游戏后即可开始任务。";
        UpdateDashboardPresentation(readyToStart);
    }

    private void UpdateDashboardPresentation(bool readyToStart)
    {
        var (statusText, statusBrushKey) = GetDashboardStatusPresentation(_workerSnapshot, readyToStart);
        var statusBrush = (WpfBrush)FindResource(statusBrushKey);
        HomeRunSummaryText.Text = statusText;
        HomeStatusIndicator.Fill = statusBrush;

        var worker = _workerSnapshot.WorkerSnapshot;
        var activeRun = _workerSnapshot.SnapshotFresh ? worker?.ActiveRun : null;
        var lastRun = _workerSnapshot.SnapshotFresh ? worker?.LastRun : null;
        if (activeRun is not null) {
            var item = GetCurrentPlanItem(activeRun);
            HomeCurrentStepText.Text = item is null
                ? GetHomeRunSummary(activeRun)
                : $"{item.TaskLabel} · {GetPlanItemStateText(item.State)}";
        } else if (lastRun is not null) {
            HomeCurrentStepText.Text = GetHomeRunSummary(lastRun);
        } else {
            HomeCurrentStepText.Text = "等待下一步";
        }

        UpdateBottomStatusBar(statusText, statusBrush);
    }

    private static (string Text, string BrushKey) GetDashboardStatusPresentation(
        WorkerCoordinatorSnapshot snapshot,
        bool readyToStart)
    {
        if (snapshot.Observation == WorkerObservation.WorkerStarting) {
            return ("正在启动", "Brush.Primary");
        }

        if (snapshot.Observation == WorkerObservation.WorkerRecoveryConflict) {
            return ("运行失败", "Brush.Error");
        }

        if (snapshot.Observation != WorkerObservation.Connected || !snapshot.SnapshotFresh) {
            return ("尚未就绪", "Brush.Text.Muted");
        }

        var worker = snapshot.WorkerSnapshot;
        if (worker?.WorkerState == WorkerState.Stopping) {
            return ("正在停止", "Brush.Warning");
        }

        if (worker?.WorkerState == WorkerState.Starting) {
            return ("正在启动", "Brush.Primary");
        }

        if (worker?.WorkerState == WorkerState.Faulted) {
            return ("运行失败", "Brush.Error");
        }

        var run = worker?.ActiveRun ?? worker?.LastRun;
        if (run is not null) {
            return run.State switch {
                RunState.Starting => ("正在启动", "Brush.Primary"),
                RunState.Running => ("运行中", "Brush.Success"),
                RunState.Stopping => ("正在停止", "Brush.Warning"),
                RunState.Failed => ("运行失败", "Brush.Error"),
                RunState.Succeeded => ("已完成", "Brush.Success"),
                RunState.Cancelled => ("已停止", "Brush.Text.Muted"),
                _ => readyToStart
                    ? ("准备就绪", "Brush.Primary")
                    : ("尚未就绪", "Brush.Text.Muted")
            };
        }

        return readyToStart
            ? ("准备就绪", "Brush.Primary")
            : ("尚未就绪", "Brush.Text.Muted");
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

    private void UpdateBottomStatusBar(string statusText, WpfBrush statusBrush)
    {
        GlobalReadyText.Text = $"整体：{statusText}";
        GlobalReadyIndicator.Fill = statusBrush;

        GlobalWorkerStatusText.Text = $"Worker：{GetHomeWorkerSummary(_workerSnapshot)}";
        GlobalWorkerIndicator.Fill = (WpfBrush)FindResource(GetWorkerStatusBrushKey(_workerSnapshot));

        GlobalSessionStatusText.Text = GetBottomSessionText(_sessionSnapshot);
        GlobalSessionIndicator.Fill = (WpfBrush)FindResource(GetSessionStatusBrushKey(_sessionSnapshot.State));

        var ipcConnected = _workerSnapshot.Observation == WorkerObservation.Connected;
        GlobalIpcStatusText.Text = ipcConnected ? "IPC：已连接" : "IPC：未连接";
        GlobalIpcIndicator.Fill = (WpfBrush)FindResource(
            ipcConnected ? "Brush.Success" : "Brush.Text.Muted");
    }

    private static string GetWorkerStatusBrushKey(WorkerCoordinatorSnapshot snapshot)
    {
        if (snapshot.Observation == WorkerObservation.Connected && snapshot.SnapshotFresh) {
            return snapshot.WorkerSnapshot?.WorkerState switch {
                WorkerState.Ready => "Brush.Success",
                WorkerState.Starting => "Brush.Primary",
                WorkerState.Stopping or WorkerState.NotReady => "Brush.Warning",
                WorkerState.Faulted => "Brush.Error",
                _ => "Brush.Primary"
            };
        }

        return snapshot.Observation switch {
            WorkerObservation.WorkerStarting => "Brush.Primary",
            WorkerObservation.IpcDisconnected or WorkerObservation.WorkerRecoveryConflict => "Brush.Error",
            _ => "Brush.Text.Muted"
        };
    }

    private static string GetBottomSessionText(ChildSessionSnapshot snapshot)
    {
        if (snapshot.ChildSessionId is uint sessionId
            && snapshot.State is (ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden)) {
            return $"Session：{sessionId}";
        }

        return $"Session：{GetStateBadgeText(snapshot.State)}";
    }

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

    private static string GetRecoveryGuidance(string operation)
    {
        if (operation.Contains("启动", StringComparison.Ordinal)) {
            return "请确认程序路径和启动参数正确，并检查桌面分身连接状态后重试。";
        }

        if (operation.Contains("桌面分身", StringComparison.Ordinal) || operation.Contains("子桌面", StringComparison.Ordinal)) {
            return "请确认程序以管理员权限运行，并检查桌面分身状态后重试。";
        }

        return "请检查当前配置和系统状态后重试。";
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
            TryOpenLogsDirectory(showError: true);
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
