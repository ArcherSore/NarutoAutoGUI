namespace NarutoAutoGUI.Views;

internal sealed class TaskPlanPresentationState
{
    internal string? ExpandedTaskName { get; private set; }

    internal void Expand(string taskName) => ExpandedTaskName = taskName;

    internal void Toggle(string taskName) => ExpandedTaskName = ExpandedTaskName == taskName ? null : taskName;

    internal void Collapse() => ExpandedTaskName = null;

    internal void Remove(string taskName)
    {
        if (ExpandedTaskName == taskName) {
            ExpandedTaskName = null;
        }
    }
}
