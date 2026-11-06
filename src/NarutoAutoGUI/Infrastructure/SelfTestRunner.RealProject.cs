using NarutoAutoGUI.ProjectModel;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // Fixtures only imitate MaaNOP's interface.json and the loader fails closed, so a field MaaNOP starts using would
    // otherwise first break on a user's machine. Opt-in because the project is not in this repository; it is only read.
    private static void VerifyRealMaaNopProject(string testDirectory)
    {
        var projectDirectory = Environment.GetEnvironmentVariable("NARUTO_MAANOP_DIR");
        if (string.IsNullOrWhiteSpace(projectDirectory)) {
            throw new SelfTestSkip("未设置 NARUTO_MAANOP_DIR，未校验真实 MaaNOP 项目。");
        }

        var project = ProjectPlanModule.Open(projectDirectory, Path.Combine(testDirectory, "real-project-config.json"));
        foreach (var selected in project.SelectedTaskNames.ToArray()) {
            project.RemoveTask(selected);
        }
        var failures = new List<string>();
        var plans = 0;
        foreach (var task in project.Tasks) {
            try {
                project.AddTask(task.Name);
                // An option only the user can fill in, such as an account number, holds the task back by design.
                var options = project.GetConfiguration(task.Name);
                if (options.GlobalOptions.Concat(options.TaskOptions).Any(WaitsForUser)) {
                    continue;
                }
                var item = project.CreateRunStartAttempt().Plan.Items.Single();
                Require(item.TaskName == task.Name && !string.IsNullOrWhiteSpace(item.Entry), "Run Plan 与任务不符。");
                plans++;
            } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException
                or ArgumentException or System.Text.Json.JsonException) {
                failures.Add($"{task.Name}：{exception.Message}");
            } finally {
                project.RemoveTask(task.Name);
            }
        }
        var name = $"{project.ProjectName} {project.ProjectVersion}";
        Require(failures.Count == 0, $"{name} 有 {failures.Count} 个任务无法生成 Run Plan：{string.Join("；", failures)}");
        Require(plans > 0, $"{name} 的 {project.Tasks.Count} 个任务都在等待用户填写，没有生成任何 Run Plan。");
    }

    private static bool WaitsForUser(ProjectOptionEditor option) =>
        option.StartBlocker is not null || option.ActiveChildren.Any(WaitsForUser);
}
