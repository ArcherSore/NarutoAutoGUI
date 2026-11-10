using NarutoAutoGUI.ProjectModel;

namespace NarutoAutoGUI.Infrastructure;

internal sealed record AutoRunTarget(Guid Id, string Name)
{
    internal static AutoRunTarget[] Of(ProjectPlanModule project) =>
        project.Configurations.Select(item => new AutoRunTarget(item.Id, item.Name)).ToArray();
}

internal sealed record AutoRunDecision(Guid? ConfigurationId, bool QuitAfterRun)
{
    internal bool Run => ConfigurationId is not null;
}

// Decides, for one launch, whether to start a Task Configuration by itself and which one.
internal static class AutoRunPlanner
{
    private static readonly AutoRunDecision Skip = new(null, false);

    // --instance only applies to --autostart, and a selector that does not resolve to exactly one configuration
    // never falls back to the configured one: a script must not run the wrong configuration.
    internal static AutoRunDecision Decide(LaunchCommand command, Guid? configured, bool onManualLaunch,
        IReadOnlyList<AutoRunTarget> configurations, bool canStart)
    {
        if (!canStart || (!command.AutoStart && !onManualLaunch)) {
            return Skip;
        }
        var target = command.AutoStart && command.Instance is { } selector
            ? Resolve(selector, configurations)
            : configurations.FirstOrDefault(item => item.Id == configured);
        return target is null ? Skip : new AutoRunDecision(target.Id, command.QuitAfterRun);
    }

    private static AutoRunTarget? Resolve(string selector, IReadOnlyList<AutoRunTarget> configurations)
    {
        if (Guid.TryParse(selector, out var id) && configurations.FirstOrDefault(item => item.Id == id) is { } byId) {
            return byId;
        }
        var named = configurations.Where(item => item.Name == selector).ToArray();
        return named.Length == 1 ? named[0] : null;
    }
}
