using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyPretaskGuiWiring(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "pretask-gui"), (window, directory) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetMethod("EndOnboarding", flags)!.Invoke(window, new object[] { true });
            var project = (ProjectPlanModule)typeof(MainWindow).GetField("_projectPlan", flags)!.GetValue(window)!;
            var catalog = Named<Panel>(window, "AvailableTasksPanel");
            var planPanel = Named<Panel>(window, "PlanItemsPanel");
            void Click(Button button)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpOnboarding();
            }
            Button ButtonNamed(DependencyObject root, string name) => ConfigurationDescendants(root)
                .OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
            Border[] Cards() => planPanel.Children.OfType<Border>()
                .Where(card => card.Tag is ProjectTaskChoice).ToArray();
            Button Header(Border card) => ConfigurationDescendants(card).OfType<Button>()
                .Single(button => button.Tag is ProjectTaskChoice && button.Style
                    == window.FindResource("PlanHeaderButtonStyle"));

            Click(Named<Button>(window, "TaskShelfHeaderButton"));
            Require(catalog.Children[0] is TextBlock { Text: "前置任务" },
                "前置任务应在添加任务面板单独显示，位于普通任务之前。");
            Click(ButtonNamed(catalog, "添加任务：第二个前置"));
            Click(ButtonNamed(catalog, "添加任务：启动并登录"));
            Require(Cards().Select(card => ((ProjectTaskChoice)card.Tag).Kind)
                    .SequenceEqual(new[] { PlanItemKind.Pretask, PlanItemKind.Pretask, PlanItemKind.MaaTask })
                && Cards().Select(card => ((ProjectTaskChoice)card.Tag).Name)
                    .SequenceEqual(new[] { "RealTask", "SecondBoot", "RealTask" }),
                "前置任务应按 PI 顺序固定在前，普通任务与前置任务允许同名。");
            Require(Cards().Where(card => ((ProjectTaskChoice)card.Tag).Kind == PlanItemKind.Pretask)
                .All(card => ConfigurationDescendants(card).OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == "拖动以调整顺序")
                    .Visibility == Visibility.Hidden), "前置任务不得提供重排入口。");

            var preCard = Cards()[0];
            Click(Header(preCard));
            preCard = Cards()[0];
            var account = ConfigurationDescendants(preCard).OfType<TextBox>()
                .Single(editor => AutomationProperties.GetName(editor) == "启动账号");
            Require(!ConfigurationDescendants(preCard).OfType<TextBox>()
                .Any(editor => AutomationProperties.GetName(editor).StartsWith("Server range")),
                "前置参数编辑不应混入普通任务的全局参数。");
            account.Text = "account-text";
            account.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, account, null) {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            });
            Require(project.Configurations[0].ExplicitOptions["LoginAccount"].GetProperty("Inputs")
                .GetProperty("uin").GetString() == "account-text",
                "前置 input 应保存原始字符串，不按 pipeline_type 转换。");
            foreach (var (index, label, expected) in new[] {
                (0, "启动并登录", "前置登录说明"), (2, "Real task", "A long task description")
            }) {
                Click(ButtonNamed(Cards()[index], $"查看 {label} 的任务描述"));
                var document = Named<FlowDocumentScrollViewer>(window, "TaskDescriptionViewer").Document;
                var text = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text;
                Require(Named<TextBlock>(window, "TaskDescriptionDrawerTitle").Text == label
                    && text.Contains(expected, StringComparison.Ordinal),
                    "同名的前置任务与普通任务应打开各自的说明。");
                Click(Named<Button>(window, "TaskDescriptionCloseButton"));
            }
            Click(Header(Cards()[2]));
            InvokeOnboarding(window, "ReplayOnboarding");
            PumpOnboarding();
            ClickOnboarding(window, "OnboardingNext");
            PumpOnboarding();
            Require(ConfigurationDescendants(Cards()[0]).OfType<TextBox>()
                .Any(editor => AutomationProperties.GetName(editor) == "启动账号"),
                "回放指引应展开位于最前的前置任务。");
            typeof(MainWindow).GetMethod("EndOnboarding", flags)!.Invoke(window, new object[] { true });
            PumpOnboarding();
            Require(ConfigurationDescendants(Cards()[2]).OfType<TextBox>().Any()
                && !ConfigurationDescendants(Cards()[0]).OfType<TextBox>().Any(),
                "回放结束应按种类恢复原先展开的普通同名任务。");

            System.Windows.Controls.CheckBox Selection(int index) => ConfigurationDescendants(Cards()[index])
                .OfType<System.Windows.Controls.CheckBox>().Single();
            void Select(int index, bool enabled)
            {
                var selection = Selection(index);
                selection.IsChecked = enabled;
                selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpOnboarding();
            }
            var configPath = Path.Combine(directory, "config", "maanop-config.json");
            var saved = File.ReadAllText(configPath);
            Require(Enumerable.Range(0, 3).All(index => Selection(index).IsChecked == true),
                "所有计划项默认勾选。");
            Select(0, false);
            Require(project.CreateRunStartAttempt().Plan.Items.Select(item => (item.Kind, item.TaskName))
                .SequenceEqual(new[] { (PlanItemKind.Pretask, "SecondBoot"), (PlanItemKind.MaaTask, "RealTask") })
                && File.ReadAllText(configPath) == saved && project.SelectedItemCount == 3,
                "临时取消前置任务不得删除配置或跳过同名普通任务，Run Plan 应只包含勾选项。");
            InvokeOnboarding(window, "RenderTaskPlan");
            Require(Selection(0).IsChecked == false, "重绘任务栏不能重置临时选择。");
            InvokeOnboarding(window, "LoadProject");
            project = (ProjectPlanModule)typeof(MainWindow).GetField("_projectPlan", flags)!.GetValue(window)!;
            Require(Selection(0).IsChecked == false && project.EnabledItems.Count == 2,
                "LoadProject 重载应保留勾选状态和过滤后的计划。");
            var originalConfiguration = project.ActiveConfigurationId;
            project.CreateConfiguration();
            project.AddPretask("RealTask");
            Require(project.IsItemEnabled(PlanItemKind.Pretask, "RealTask"), "新配置不继承其他配置的临时选择。");
            project.ActivateConfiguration(originalConfiguration);
            Require(!project.IsItemEnabled(PlanItemKind.Pretask, "RealTask"), "切换配置应保留各自的临时选择。");
            Require(ProjectPlanModule.Open(directory, configPath).EnabledItems.Count == 3,
                "重新打开项目应恢复全部勾选。");
            foreach (var kind in new[] { PlanItemKind.Pretask, PlanItemKind.MaaTask }) {
                project.SetItemEnabled(kind, "RealTask", false);
                if (kind == PlanItemKind.Pretask) {
                    project.RemovePretask("RealTask");
                    project.AddPretask("RealTask");
                } else {
                    project.RemoveTask("RealTask");
                    project.AddTask("RealTask");
                }
                Require(project.IsItemEnabled(kind, "RealTask"), "删除后重新添加任务必须恢复勾选。");
            }
            project.SetItemEnabled(PlanItemKind.Pretask, "RealTask", false);
            InvokeOnboarding(window, "RenderTaskPlan");
            Select(1, false);
            Select(2, false);
            Require(project.EnabledItems.Count == 0 && !Named<Button>(window, "StartTaskHeaderButton").IsEnabled,
                "全部取消后不得启动空计划。");
            try {
                project.CreateRunStartAttempt();
                throw new InvalidDataException("全部取消后仍生成了运行计划。");
            } catch (InvalidOperationException) {
            }
            for (var index = 0; index < 3; index++) {
                Select(index, true);
            }

            var started = DateTime.UtcNow.AddSeconds(-5);
            var plan = project.CreateRunStartAttempt().Plan;
            var states = new[] { PlanItemState.Succeeded, PlanItemState.Running, PlanItemState.Pending };
            PlanItemSnapshot Snapshot(RunPlanItem item, PlanItemState state, DateTime? startedAt, DateTime? endedAt) =>
                new(item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry, item.ResolvedOptions,
                    item.PipelineOverride, state, startedAt, endedAt, null, null, null) { Kind = item.Kind };
            var run = new RunSnapshot(Guid.NewGuid(), "digest", RunState.Running, started, started, null, null,
                plan.Items[1].PlanItemId, 1, plan, plan.Items.Select((item, index) => Snapshot(item, states[index],
                    index < 2 ? started : null, index == 0 ? started : null)).ToArray(), null, null);
            var record = new WorkerAdmissionRecord(Guid.NewGuid(), "token", 7, Environment.ProcessId,
                project.RuntimeProfileDigest, DateTime.UtcNow);
            var idle = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
            void Observe(WorkerSnapshot worker)
            {
                SetOnboardingField(window, "_sessionSnapshot",
                    new ChildSessionSnapshot(ChildSessionState.ConnectedHidden, 7, 1, "fixture"));
                SetOnboardingField(window, "_workerSnapshot",
                    new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, worker, ""));
                InvokeOnboarding(window, "RefreshRuntimeControls");
                PumpOnboarding();
            }
            Observe(idle with { RunState = RunState.Running, ActiveRun = run });
            Require(!Selection(0).IsEnabled && !Selection(2).IsEnabled
                && Named<TextBlock>(window, "RuntimeStatusTitle").Text == "第二个前置"
                && Named<Button>(window, "StopTaskHeaderButton").IsEnabled,
                "运行期间勾选被锁定，控制面板显示当前前置任务并可停止。");
            var lastWorker = idle with { LastRun = run with { State = RunState.Succeeded } };
            var kinds = project.SelectedItems.Select(item => (item.Kind, item.Name)).ToArray();
            Require(RunProgress.Derive(lastWorker, kinds) is not null
                && RunProgress.Derive(lastWorker, kinds.Select(item => (PlanItemKind.MaaTask, item.Name))
                    .ToArray()) is null, "Last Run 匹配必须包含种类，不能只看任务名称。");

            Observe(idle);
            Select(0, false);
            var filtered = project.CreateRunStartAttempt().Plan;
            var filteredRun = run with {
                Plan = filtered, CurrentPlanItemId = filtered.Items[0].PlanItemId, CurrentPlanItemIndex = 0,
                Items = filtered.Items.Select((item, index) => Snapshot(item,
                    index == 0 ? PlanItemState.Running : PlanItemState.Pending,
                    index == 0 ? started : null, null)).ToArray()
            };
            Observe(idle with { RunState = RunState.Running, ActiveRun = filteredRun });
            Require(Named<TextBlock>(window, "RuntimeStatusTitle").Text == "第二个前置"
                && Named<TextBlock>(window, "RunProgressCaption").Text == "运行中 · 第 1 / 2 项",
                "跳过前置项后，控制面板应显示实际运行项和过滤后的进度。");
            Observe(idle);
            Select(0, true);
            Click(ButtonNamed(Cards()[2], "从执行计划移除 Real task"));
            Require(project.SelectedTaskNames.Count == 0 && project.SelectedItemCount == 2
                && Named<Button>(window, "StartTaskHeaderButton").IsEnabled,
                "只选择前置任务时仍可开始，移除普通同名任务不得影响前置任务。");
            var onlyPre = project.CreateRunStartAttempt().Plan;
            var starting = run with {
                State = RunState.Starting, CurrentPlanItemId = onlyPre.Items[0].PlanItemId, CurrentPlanItemIndex = 0,
                Plan = onlyPre, Items = onlyPre.Items.Select((item, index) => index == 0
                    ? Snapshot(item, PlanItemState.Starting, started, null)
                    : Snapshot(item, PlanItemState.Pending, null, null)).ToArray()
            };
            Observe(idle with { RunState = RunState.Starting, ActiveRun = starting });
            Require(Named<Button>(window, "StopTaskHeaderButton").IsEnabled,
                "前置任务启动阶段也必须提供停止，不能等待游戏控制器创建。");
            Observe(idle);
            Click(ButtonNamed(Cards()[0], "从执行计划移除 启动并登录"));
            Require(project.SelectedPretaskNames.SequenceEqual(new[] { "SecondBoot" }),
                "前置任务的移除入口必须更新当前配置。");
        }, beforeLoad: directory => {
            var path = Path.Combine(directory, "interface.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            root["pretask"] = JsonNode.Parse("""
                [
                  {"name":"RealTask","label":"启动并登录","description":"前置登录说明",
                   "exec":"python","option":["LoginAccount"]},
                  {"name":"SecondBoot","label":"第二个前置","exec":"python"}
                ]
                """);
            root["option"]!["LoginAccount"] = JsonNode.Parse("""
                {"type":"input","label":"启动账号","inputs":[
                  {"name":"uin","label":"账号","default":"123","pipeline_type":"int"}
                ]}
                """);
            File.WriteAllText(path, root.ToJsonString());
        });
    }
}
