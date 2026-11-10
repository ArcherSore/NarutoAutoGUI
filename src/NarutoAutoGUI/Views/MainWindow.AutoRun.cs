using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private readonly LaunchCommand _launchCommand;

    // Raised just before the launch starts a configuration by itself, so --quit-after-run can start watching.
    internal event EventHandler<AutoRunDecision>? AutoRunStarted;

    // Raised when something the launch started on its own failed. The text is for a notification.
    internal event EventHandler<string>? AutoRunFailed;

    // Raised when an auto-run ended without any Run having begun, so --quit-after-run has nothing to wait for.
    internal event EventHandler? AutoRunAbandoned;

    private int _acceptedRuns;

    // Starts the configuration this launch asks for, if any: activates it, prepares the Runtime Environment and
    // starts the Run, as the Start button does. Returns whether it did.
    internal async Task<bool> AutoRunOnLaunchAsync()
    {
        if (_projectPlan is not { } project) {
            return false;
        }
        var canStart = AutoRunPlanner.RuntimeAllows(CurrentControls);
        var decision = AutoRunPlanner.Decide(
            _launchCommand, _settings.AutoRunConfigurationId, _settings.AutoRunOnManualLaunch.Value,
            AutoRunTarget.Of(project), canStart);
        if (decision.ConfigurationId is not { } id) {
            if (_launchCommand.AutoStart || _settings.AutoRunOnManualLaunch.Value) {
                _logger.Info("自动执行已跳过：没有可执行的目标配置，或当前不能开始任务。");
            }
            return false;
        }
        // A refusal is reported by the change itself: the other configuration must never be run instead.
        if (!ChangeConfiguration(() => project.ActivateConfiguration(id), unattended: true)) {
            return true;
        }
        if (project.EnabledItems.Count == 0) {
            ReportUnattendedFailure("自动执行", new InvalidOperationException("所选配置没有可执行的任务。"));
            return true;
        }
        if (!_projectConfigurationValid) {
            ReportUnattendedFailure("自动执行", new InvalidOperationException("所选配置未通过校验，请打开程序检查参数。"));
            return true;
        }
        _logger.Info($"自动执行：已激活配置并开始任务{AppLogger.Field("configuration", ConfigurationName(id))}");
        var acceptedBefore = _acceptedRuns;
        AutoRunStarted?.Invoke(this, decision);
        if (CurrentControls.Action is RuntimeAction.Start) {
            await RunOperationAsync(PendingOperation.StartingRun, () => StartCurrentPlanAsync(), unattended: true);
        } else {
            await PrepareFromPanelAsync(startRun: true, unattended: true);
        }
        // Failed, refused or cancelled by an exit request: no Run began, whatever the reason.
        if (_acceptedRuns == acceptedBefore) {
            AutoRunAbandoned?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    // Loaded starts everything and needs a shown window, so the window is shown and hidden before it is ever drawn.
    internal void StartInTray()
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        Show();
        Hide();
    }

    private void ReportUnattendedFailure(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        AutoRunFailed?.Invoke(this, $"{operation}：{exception.GetBaseException().Message}");
    }
}
