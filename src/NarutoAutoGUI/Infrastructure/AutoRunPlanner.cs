using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal sealed record AutoRunTarget(Guid Id, string Name)
{
    internal static AutoRunTarget[] Of(ProjectPlanModule project) =>
        project.Configurations.Select(item => new AutoRunTarget(item.Id, item.Name)).ToArray();
}

// SkipReason is set when the launch asked for an auto-run that cannot happen; it is for the log.
internal sealed record AutoRunDecision(Guid? ConfigurationId, string? SkipReason = null);

// Decides, for one launch, whether to start a Task Configuration by itself and which one.
internal static class AutoRunPlanner
{
    // Whether the runtime allows a Run at all. What the active configuration holds does not count: it is about to be
    // replaced by the one that was asked for. A Retry that would start the Run is a ready runtime whose last Run failed.
    internal static bool RuntimeAllows(RuntimeControlState controls) => controls.ConfigurationEditable
        && controls.Action is RuntimeAction.Prepare { Enabled: true }
            or RuntimeAction.Start { Blocker: null or StartBlocker.NoTasks or StartBlocker.ConfigurationInvalid }
            or RuntimeAction.Retry {
                Target: RetryTarget.Start, Blocker: null or StartBlocker.NoTasks or StartBlocker.ConfigurationInvalid
            };

    // --instance only applies to --autostart, and a selector that does not resolve to exactly one configuration
    // never falls back to the configured one: a script must not run the wrong configuration.
    internal static AutoRunDecision Decide(LaunchCommand command, Guid? configured, bool onManualLaunch,
        IReadOnlyList<AutoRunTarget> configurations, bool canStart)
    {
        if (!command.AutoStart && !onManualLaunch) {
            return Skip(null);
        }
        if (!canStart) {
            return Skip("运行环境当前不能准备或开始任务");
        }
        var byInstance = command.AutoStart && command.Instance is not null;
        var target = byInstance
            ? Resolve(command.Instance!, configurations)
            : configurations.FirstOrDefault(item => item.Id == configured);
        if (target is not null) {
            return new AutoRunDecision(target.Id);
        }
        return Skip(byInstance
            ? $"--instance \"{command.Instance}\" 没有匹配到唯一的配置"
            : "没有选择要自动执行的配置，或所选配置已不存在");
    }

    private static AutoRunDecision Skip(string? reason) => new(null, reason);

    private static AutoRunTarget? Resolve(string selector, IReadOnlyList<AutoRunTarget> configurations)
    {
        if (Guid.TryParse(selector, out var id) && configurations.FirstOrDefault(item => item.Id == id) is { } byId) {
            return byId;
        }
        var named = configurations.Where(item => item.Name == selector).ToArray();
        return named.Length == 1 ? named[0] : null;
    }
}
