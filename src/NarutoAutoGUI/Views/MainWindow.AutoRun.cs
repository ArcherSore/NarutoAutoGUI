using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private readonly LaunchCommand _launchCommand;

    // Raised just before the launch starts a configuration by itself, so --quit-after-run can start watching.
    internal event EventHandler<AutoRunDecision>? AutoRunStarted;

    // Raised when a launch-started operation failed before any Run began. The text is for a notification.
    internal event EventHandler<string>? AutoRunFailed;

    // Starts the configuration this launch asks for, if any: activates it, prepares the Runtime Environment and
    // starts the Run, as the Start button does. Returns whether it did.
    internal async Task<bool> AutoRunOnLaunchAsync()
    {
        if (_projectPlan is not { } project) {
            return false;
        }
        var canStart = CurrentControls.Action is RuntimeAction.Prepare { Enabled: true }
            or RuntimeAction.Start { Enabled: true };
        var decision = AutoRunPlanner.Decide(
            _launchCommand, _settings.AutoRunConfigurationId, _settings.AutoRunOnManualLaunch.Value,
            project.Configurations.Select(item => new AutoRunTarget(item.Id, item.Name)).ToArray(), canStart);
        if (decision.ConfigurationId is not { } id) {
            if (_launchCommand.AutoStart || _settings.AutoRunOnManualLaunch.Value) {
                _logger.Info("自动执行已跳过：没有可执行的目标配置，或当前不能开始任务。");
            }
            return false;
        }
        ChangeConfiguration(() => project.ActivateConfiguration(id));
        if (!_projectConfigurationValid) {
            ReportUnattendedFailure("自动执行", new InvalidOperationException("所选配置未通过校验，请打开程序检查参数。"));
            return true;
        }
        _logger.Info($"自动执行：已激活配置并开始任务{AppLogger.Field("configuration", ConfigurationName(id))}");
        AutoRunStarted?.Invoke(this, decision);
        if (CurrentControls.Action is RuntimeAction.Start) {
            await RunOperationAsync(PendingOperation.StartingRun, () => StartCurrentPlanAsync(), unattended: true);
        } else {
            await PrepareFromPanelAsync(startRun: true, unattended: true);
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
