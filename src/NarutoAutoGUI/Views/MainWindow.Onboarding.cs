using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private OnboardingPreferences? _onboardingPreferences;
    private bool _onboardingStartupCompleted;
    private bool _onboardingAutoEnded;
    private bool _onboardingActive;
    private bool _onboardingReplay;
    private bool _onboardingPaused;
    private bool _onboardingShown;
    private bool _onboardingChangingStep;
    private bool _onboardingClosed;
    private int _onboardingStep;
    private CancellationTokenSource? _onboardingStartCancellation;
    private CancellationTokenSource? _onboardingStepCancellation;
    private DispatcherOperation? _onboardingLayoutOperation;
    private IInputElement? _onboardingPreviousFocus;
    private string? _onboardingPreviousExpandedTask;
    private bool _onboardingPreviousShelfExpanded;
    private bool _onboardingPreviousPreviewExpanded;
    private double _onboardingPreviousScroll;
    private readonly RectangleGeometry _onboardingHole = new();
    private Rect _onboardingTargetBounds = Rect.Empty;
    private Rect _onboardingWindowBounds = Rect.Empty;
    private (Rect Popover, Rect Target) _onboardingArrowBounds = (Rect.Empty, Rect.Empty);
    private readonly Dictionary<string, FrameworkElement> _taskDescriptionButtons = new(StringComparer.Ordinal);

    private bool IsOnboardingVisible => _onboardingActive && !_onboardingPaused
        && OnboardingOverlay.Visibility == Visibility.Visible;

    private bool CanPresentOnboarding => !_onboardingClosed && !_exitInProgress && !_busy
        && IsLoaded && IsVisible && WindowState != WindowState.Minimized
        && HomeView.Visibility == Visibility.Visible && _projectPlan is not null
        && TaskWorkspacePanel.Visibility == Visibility.Visible && !IsGlobalModalOpen
        && TaskDescriptionOverlay.Visibility != Visibility.Visible
        && _sessionSnapshot.State is not (ChildSessionState.Connecting or ChildSessionState.Disconnecting);

    private void ReplayOnboarding()
    {
        if (_projectPlan is null || _busy || _exitInProgress || IsGlobalModalOpen || _onboardingActive) {
            return;
        }
        _onboardingAutoEnded = true;
        CancelOnboardingStart();
        StartOnboarding(replay: true);
    }

    private void StartOnboarding(bool replay)
    {
        try {
            _onboardingPreviousFocus = Keyboard.FocusedElement;
            _onboardingReplay = replay;
            _onboardingPreviousExpandedTask = _expandedTaskName;
            _onboardingPreviousShelfExpanded = _taskShelfExpanded;
            _onboardingPreviousPreviewExpanded = PreviewCardContent.Visibility == Visibility.Visible;
            _onboardingPreviousScroll = PlanScroll.VerticalOffset;
            _onboardingActive = true;
            _onboardingPaused = false;
            _onboardingShown = false;
            _onboardingStep = 0;
            _settings.ReplayOnboarding.Status = string.Empty;
            SwitchSection(MainSection.Home);
            WindowOverlayRoot.LayoutUpdated += Onboarding_LayoutUpdated;
            PreviewGotKeyboardFocus += Onboarding_PreviewGotKeyboardFocus;
            SystemParameters.StaticPropertyChanged += Onboarding_SystemParametersChanged;
            _ = ShowOnboardingStepAsync(animate: false);
        } catch (Exception exception) {
            FailOnboarding(exception);
        }
    }

    private async Task ShowOnboardingStepAsync(bool animate, bool playPulse = true)
    {
        CancelOnboardingStep();
        var cancellation = new CancellationTokenSource();
        _onboardingStepCancellation = cancellation;
        _onboardingChangingStep = true;
        try {
            if (animate && IsOnboardingVisible && SystemParameters.ClientAreaAnimation) {
                OnboardingPopover.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(60)));
                await Task.Delay(60, cancellation.Token);
            }
            if (_onboardingStep == 0 || _onboardingStep == 1 && _projectPlan!.SelectedTaskNames.Count == 0) {
                SetTaskShelfExpanded(true);
                AvailableTasksScroll.ScrollToTop();
            }
            if (_onboardingReplay && _onboardingStep == 1 && _projectPlan!.SelectedTaskNames.Count > 0) {
                _expandedTaskName = _projectPlan.SelectedTaskNames[0];
                RenderTaskPlan();
            }
            if (_onboardingStep == 3) {
                SetPreviewExpanded(true);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellation.Token);
            if (_onboardingStep == 1) {
                var card = ResolveOnboardingTarget(1);
                // A card taller than the viewport should retain its title and description entry.
                card.BringIntoView(new Rect(0, 0, card.ActualWidth,
                    Math.Min(card.ActualHeight, PlanScroll.ViewportHeight)));
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!CanPresentOnboarding) {
                PauseOnboarding();
                return;
            }
            for (var step = 0; step < 4; step++) {
                _ = OnboardingBounds(ResolveOnboardingTarget(step));
            }
            SetOnboardingText();
            OnboardingOverlay.Visibility = Visibility.Visible;
            _onboardingPaused = false;
            UpdateOnboardingGeometry(animate);
            _onboardingShown = true;
            _onboardingChangingStep = false;
            OnboardingNext.Focus();
            if (SystemParameters.ClientAreaAnimation) {
                OnboardingPopover.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            }
            if (playPulse && _onboardingStep == 1 && SystemParameters.ClientAreaAnimation) {
                await Task.Delay(450, cancellation.Token);
                await PlayOnboardingPulseAsync(cancellation.Token);
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception exception) {
            FailOnboarding(exception);
        } finally {
            if (_onboardingStepCancellation == cancellation) {
                _onboardingStepCancellation = null;
                _onboardingChangingStep = false;
            }
            cancellation.Dispose();
        }
    }

    private FrameworkElement ResolveOnboardingTarget(int step)
    {
        if (step == 0) {
            return TaskWorkspacePanel;
        }
        if (step == 2) {
            return RuntimeControlCard;
        }
        if (step == 3) {
            return PreviewCard;
        }
        var first = _projectPlan?.SelectedTaskNames.FirstOrDefault();
        if (first is not null && _planItemContainers.TryGetValue(first, out var card)) {
            return card;
        }
        if (_onboardingReplay && first is null) {
            return TaskShelfContent;
        }
        throw new InvalidOperationException("新手指引找不到第一张任务卡。");
    }

    private void SetOnboardingText()
    {
        var empty = _projectPlan!.SelectedTaskNames.Count == 0;
        (OnboardingTitle.Text, OnboardingDescription.Text) = _onboardingStep switch {
            0 => ("配置自动化任务", "从“可用任务”添加要执行的任务，并调整执行顺序。\n顶部标签可以保存多套独立配置。"),
            1 when empty => ("查看说明并设置参数", "添加任务后，会在执行计划中出现任务卡片。\n"
                + "你可以通过 ⓘ 查看任务说明，并在卡片中调整参数。"),
            1 => ("查看说明并设置参数", "点击 ⓘ 查看任务说明，并在卡片中调整运行参数。\n"
                + "开始前建议先确认任务所需的游戏初始界面。"),
            2 => ("启动自动化", "配置完成后从这里开始。\n按钮会根据当前状态自动切换为准备环境、开始任务或停止任务。"),
            _ => ("查看挂机画面", "运行环境启动后，可以在这里实时查看游戏画面。\n"
                + "也可以放大预览，或显示桌面分身进行操作。")
        };
        OnboardingCounter.Text = $"{_onboardingStep + 1} / 4";
        AutomationProperties.SetName(OnboardingCounter, $"第 {_onboardingStep + 1} 步，共 4 步");
        AutomationProperties.SetName(OnboardingPopover, OnboardingTitle.Text);
        OnboardingPrevious.Visibility = _onboardingStep == 0 ? Visibility.Collapsed : Visibility.Visible;
        OnboardingSkip.Visibility = _onboardingStep == 3 ? Visibility.Collapsed : Visibility.Visible;
        OnboardingNext.Content = _onboardingStep == 3 ? "开始使用" : "下一步";
        AutomationProperties.SetName(OnboardingNext, _onboardingStep == 3 ? "开始使用" : "下一步");
    }

    private Rect OnboardingBounds(FrameworkElement target)
    {
        if (!target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0
            || !WindowOverlayRoot.IsAncestorOf(target)) {
            throw new InvalidOperationException($"新手指引目标未完成布局：{target.Name}。");
        }
        return target.TransformToAncestor(WindowOverlayRoot)
            .TransformBounds(new Rect(new WpfSize(target.ActualWidth, target.ActualHeight)));
    }

    private Rect VisibleOnboardingBounds(FrameworkElement target, double padding = 0)
    {
        var bounds = OnboardingBounds(target);
        var clip = OnboardingClipBounds(target);
        bounds.Intersect(clip);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) {
            throw new InvalidOperationException("新手指引目标不在可见区域。");
        }
        bounds.Inflate(padding, padding);
        bounds.Intersect(clip);
        return bounds;
    }

    private Rect OnboardingClipBounds(FrameworkElement target)
    {
        var bounds = new Rect(new WpfSize(WindowOverlayRoot.ActualWidth, WindowOverlayRoot.ActualHeight));
        for (var parent = VisualTreeHelper.GetParent(target); parent != WindowOverlayRoot && parent is not null;
            parent = VisualTreeHelper.GetParent(parent)) {
            if (parent is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter)) {
                bounds.Intersect(OnboardingBounds(element));
            }
        }
        return bounds;
    }

    private void UpdateOnboardingGeometry(bool animate = false)
    {
        var target = ResolveOnboardingTarget(_onboardingStep);
        var bounds = VisibleOnboardingBounds(target, padding: 8);
        var window = new Rect(new WpfSize(WindowOverlayRoot.ActualWidth, WindowOverlayRoot.ActualHeight));
        var changed = bounds != _onboardingTargetBounds || window != _onboardingWindowBounds;
        if (changed) {
            var previous = _onboardingHole.Rect;
            _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty, null);
            _onboardingHole.Rect = bounds;
            _onboardingHole.RadiusX = _onboardingHole.RadiusY = target is Border border
                ? border.CornerRadius.TopLeft + 4 : 8;
            if (animate && SystemParameters.ClientAreaAnimation && !previous.IsEmpty && previous.Width > 0) {
                _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty,
                    new RectAnimation(previous, bounds, TimeSpan.FromMilliseconds(200)) {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            _onboardingTargetBounds = bounds;
            _onboardingWindowBounds = window;
            OnboardingDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(window), _onboardingHole);
            OnboardingOutline.Data = _onboardingHole;
        }
        PlaceOnboardingPopover(window, bounds);
        PositionOnboardingPulse();
    }

    private void PlaceOnboardingPopover(Rect window, Rect target)
    {
        const double inset = 16;
        const double gap = 12;
        var topInset = Math.Max(inset, OnboardingBounds(MainNavigation).Top + inset);
        OnboardingPopover.Width = Math.Min(312, Math.Max(1, window.Width - inset * 2));
        OnboardingPopover.MaxHeight = Math.Max(1, window.Height - topInset - inset);
        OnboardingPopover.Measure(new WpfSize(OnboardingPopover.Width, OnboardingPopover.MaxHeight));
        var size = OnboardingPopover.DesiredSize;
        var center = new WpfPoint(target.X + target.Width / 2, target.Y + target.Height / 2);
        var left = new Rect(target.Left - gap - size.Width, center.Y - size.Height / 2, size.Width, size.Height);
        var right = new Rect(target.Right + gap, center.Y - size.Height / 2, size.Width, size.Height);
        var bottom = new Rect(center.X - size.Width / 2, target.Bottom + gap, size.Width, size.Height);
        var top = new Rect(center.X - size.Width / 2, target.Top - gap - size.Height, size.Width, size.Height);
        var candidates = _onboardingStep < 2 ? new[] { right, left, bottom, top } : [left, right, bottom, top];
        Rect chosen = Rect.Empty;
        var leastOverlap = double.MaxValue;
        foreach (var candidate in candidates) {
            var placed = new Rect(
                Math.Clamp(candidate.X, inset, Math.Max(inset, window.Width - inset - size.Width)),
                Math.Clamp(candidate.Y, topInset, Math.Max(topInset, window.Height - inset - size.Height)),
                size.Width, size.Height);
            var intersection = Rect.Intersect(placed, target);
            var overlap = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
            if (overlap < leastOverlap) {
                leastOverlap = overlap;
                chosen = placed;
            }
            if (overlap == 0) {
                break;
            }
        }
        if (OnboardingPopover.RenderTransform is not TranslateTransform current
            || current.X != chosen.X || current.Y != chosen.Y) {
            OnboardingPopover.RenderTransform = new TranslateTransform(chosen.X, chosen.Y);
        }
        DrawOnboardingArrow(chosen, target, leastOverlap == 0);
    }

    private void DrawOnboardingArrow(Rect popover, Rect target, bool visible)
    {
        OnboardingArrow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || _onboardingArrowBounds == (popover, target)) {
            return;
        }
        WpfPoint[] points;
        if (popover.Left >= target.Right || popover.Right <= target.Left) {
            var x = popover.Left >= target.Right ? popover.Left : popover.Right;
            var y = Math.Clamp(target.Y + target.Height / 2, popover.Top + 16, popover.Bottom - 16);
            points = [new(x, y - 7), new(x + (x == popover.Left ? -8 : 8), y), new(x, y + 7)];
        } else {
            var y = popover.Top >= target.Bottom ? popover.Top : popover.Bottom;
            var x = Math.Clamp(target.X + target.Width / 2, popover.Left + 16, popover.Right - 16);
            points = [new(x - 7, y), new(x, y + (y == popover.Top ? -8 : 8)), new(x + 7, y)];
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            context.LineTo(points[1], isStroked: true, isSmoothJoin: false);
            context.LineTo(points[2], isStroked: true, isSmoothJoin: false);
        }
        OnboardingArrow.Data = geometry;
        _onboardingArrowBounds = (popover, target);
    }

    private void Onboarding_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsOnboardingVisible || _onboardingChangingStep
            || _onboardingLayoutOperation is { Status: DispatcherOperationStatus.Pending }) {
            return;
        }
        _onboardingLayoutOperation = Dispatcher.BeginInvoke(() => {
            if (!IsOnboardingVisible || _onboardingChangingStep) {
                return;
            }
            try {
                UpdateOnboardingGeometry();
            } catch (Exception exception) {
                FailOnboarding(exception);
            }
        }, DispatcherPriority.Background);
    }

    private async Task PlayOnboardingPulseAsync(CancellationToken cancellation, int count = 3)
    {
        if (!PositionOnboardingPulse()) {
            return;
        }
        var repetitions = Math.Clamp(count, 1, 3);
        for (var index = 0; index < repetitions; index++) {
            cancellation.ThrowIfCancellationRequested();
            if (!IsOnboardingVisible || !SystemParameters.ClientAreaAnimation) {
                return;
            }
            OnboardingPulse.Visibility = Visibility.Visible;
            var duration = TimeSpan.FromMilliseconds(850);
            var opacity = new DoubleAnimationUsingKeyFrames();
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.38, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration)));
            OnboardingPulse.BeginAnimation(OpacityProperty, opacity);
            var scale = (ScaleTransform)OnboardingPulse.RenderTransform;
            var expansion = new DoubleAnimation(1, 32.0 / 24, duration) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, expansion);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, expansion);
            await Task.Delay(duration, cancellation);
            if (index + 1 < repetitions) {
                await Task.Delay(200, cancellation);
            }
        }
        StopOnboardingPulse();
    }

    private bool PositionOnboardingPulse()
    {
        var first = _projectPlan?.SelectedTaskNames.FirstOrDefault();
        if (_onboardingStep != 1 || first is null || !_taskDescriptionButtons.TryGetValue(first, out var icon)) {
            return false;
        }
        Rect bounds;
        try {
            bounds = VisibleOnboardingBounds(icon);
        } catch (InvalidOperationException) {
            StopOnboardingPulse();
            return false;
        }
        Canvas.SetLeft(OnboardingPulse, bounds.Left + bounds.Width / 2 - OnboardingPulse.Width / 2);
        Canvas.SetTop(OnboardingPulse, bounds.Top + bounds.Height / 2 - OnboardingPulse.Height / 2);
        return true;
    }

    private void StopOnboardingPulse()
    {
        OnboardingPulse.Visibility = Visibility.Collapsed;
        OnboardingPulse.BeginAnimation(OpacityProperty, null);
        var scale = (ScaleTransform)OnboardingPulse.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void Onboarding_SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) {
            StopOnboardingPulse();
            _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty, null);
            OnboardingPopover.BeginAnimation(OpacityProperty, null);
        }
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (!IsOnboardingVisible || _onboardingChangingStep) {
            return;
        }
        if (_onboardingStep == 3) {
            EndOnboarding(handled: true);
        } else {
            _onboardingStep++;
            _ = ShowOnboardingStepAsync(animate: true);
        }
    }

    private void OnboardingPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (IsOnboardingVisible && !_onboardingChangingStep && _onboardingStep > 0) {
            _onboardingStep--;
            _ = ShowOnboardingStepAsync(animate: true);
        }
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e) => EndOnboarding(handled: true);

    private void Onboarding_BlockDrop(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Onboarding_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsOnboardingVisible && e.NewFocus is DependencyObject target && target != OnboardingPopover
            && !OnboardingPopover.IsAncestorOf(target)) {
            e.Handled = true;
        }
    }

    private void FailOnboarding(Exception exception)
    {
        _logger.Warn($"新手指引已取消：mode={(_onboardingReplay ? "replay" : "auto")}，step={_onboardingStep + 1}。",
            exception);
        _settings.ReplayOnboarding.Status = "暂时无法显示新手指引，请稍后重试。";
        EndOnboarding(handled: false);
    }

    private void EndOnboarding(bool handled)
    {
        var markHandled = handled && _onboardingActive && _onboardingShown && !_onboardingReplay;
        var restoreReplay = _onboardingActive && _onboardingReplay && !_onboardingClosed && !_exitInProgress;
        _onboardingAutoEnded = true;
        _onboardingActive = false;
        _onboardingChangingStep = false;
        CancelOnboardingStart();
        CancelOnboardingStep();
        _onboardingLayoutOperation?.Abort();
        WindowOverlayRoot.LayoutUpdated -= Onboarding_LayoutUpdated;
        PreviewGotKeyboardFocus -= Onboarding_PreviewGotKeyboardFocus;
        SystemParameters.StaticPropertyChanged -= Onboarding_SystemParametersChanged;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _onboardingTargetBounds = Rect.Empty;
        try {
            if (restoreReplay) {
                _expandedTaskName = _projectPlan?.SelectedTaskNames
                    .Contains(_onboardingPreviousExpandedTask ?? "") == true
                    ? _onboardingPreviousExpandedTask : null;
                SetTaskShelfExpanded(_onboardingPreviousShelfExpanded);
                SetPreviewExpanded(_onboardingPreviousPreviewExpanded);
                if (_projectPlan is not null) {
                    RenderTaskPlan();
                }
                PlanScroll.ScrollToVerticalOffset(_onboardingPreviousScroll);
                SwitchSection(MainSection.Home);
            }
            if (!_onboardingClosed && !_exitInProgress) {
                if (_onboardingPreviousFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true }
                    element) {
                    element.Focus();
                } else {
                    HomeNavigationItem.Focus();
                }
            }
        } catch (Exception exception) {
            Keyboard.ClearFocus();
            _logger.Warn("恢复新手指引前的界面状态失败。", exception);
        } finally {
            _onboardingPreviousFocus = null;
        }
        if (markHandled && _onboardingPreferences?.MarkHandled() == false) {
            _settings.ReplayOnboarding.Status = "未能保存新手指引状态，下次启动可能再次显示。";
        }
        ReevaluateOnboarding();
    }

    private void CancelOnboardingStep()
    {
        _onboardingStepCancellation?.Cancel();
        _onboardingStepCancellation = null;
        StopOnboardingPulse();
        _onboardingHole.BeginAnimation(RectangleGeometry.RectProperty, null);
        OnboardingPopover.BeginAnimation(OpacityProperty, null);
    }

    private void CancelOnboardingStart()
    {
        _onboardingStartCancellation?.Cancel();
        _onboardingStartCancellation = null;
    }

    private void PauseOnboarding()
    {
        CancelOnboardingStep();
        _onboardingPaused = true;
        _onboardingChangingStep = false;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    private void ReevaluateOnboarding()
    {
        if (_logger is null || _settings is null || _onboardingClosed) {
            return;
        }
        _settings.ReplayOnboarding.IsEnabled = _projectPlan is not null && !_busy && !_exitInProgress
            && !_onboardingActive && !IsGlobalModalOpen;
        _settings.ReplayOnboarding.ToolTip = _projectPlan is null ? "项目加载后可查看新手指引"
            : _busy || _exitInProgress ? "当前操作完成后可查看新手指引" : null;
        if (_onboardingActive) {
            if (!CanPresentOnboarding) {
                PauseOnboarding();
            } else if (_onboardingPaused) {
                _onboardingPaused = false;
                _ = ShowOnboardingStepAsync(animate: false, playPulse: false);
            }
            return;
        }
        var ready = !_onboardingAutoEnded && _onboardingStartupCompleted && CanPresentOnboarding
            && _onboardingPreferences?.ShouldOfferAutomatically == true && _projectPlan!.LoadWarning is null;
        if (!ready) {
            CancelOnboardingStart();
        } else if (_onboardingStartCancellation is null) {
            _ = StartOnboardingAfterLayoutAsync();
        }
    }

    private async Task StartOnboardingAfterLayoutAsync()
    {
        var cancellation = new CancellationTokenSource();
        _onboardingStartCancellation = cancellation;
        try {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellation.Token);
            await Task.Delay(400, cancellation.Token);
            if (CanPresentOnboarding && !_onboardingAutoEnded && !_onboardingActive) {
                StartOnboarding(replay: false);
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception exception) {
            FailOnboarding(exception);
        } finally {
            if (_onboardingStartCancellation == cancellation) {
                _onboardingStartCancellation = null;
            }
            cancellation.Dispose();
        }
    }
}
