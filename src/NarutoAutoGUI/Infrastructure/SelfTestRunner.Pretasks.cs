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
            string Status(Border card) => ConfigurationDescendants(card).OfType<TextBlock>()
                .Single(text => text.Margin == new Thickness(4, 0, 6, 0)).Text;
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

            var started = DateTime.UtcNow.AddSeconds(-5);
            var plan = project.CreateRunStartAttempt().Plan;
            var states = new[] { PlanItemState.Succeeded, PlanItemState.Running, PlanItemState.Pending };
            PlanItemSnapshot Snapshot(RunPlanItem item, int index) => new(item.PlanItemId, item.TaskName,
                item.TaskLabel, item.Entry, item.ResolvedOptions, item.PipelineOverride, states[index],
                index < 2 ? started : null, index == 0 ? started : null, null, null, null) { Kind = item.Kind };
            var run = new RunSnapshot(Guid.NewGuid(), "digest", RunState.Running, started, started, null, null,
                plan.Items[1].PlanItemId, 1, plan, plan.Items.Select(Snapshot).ToArray(), null, null);
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
            Require(Status(Cards()[0]) == "成功" && Status(Cards()[2]) == "等待中"
                && Status(Cards()[1]).StartsWith("运行中", StringComparison.Ordinal)
                && Named<Button>(window, "StopTaskHeaderButton").IsEnabled,
                "同名的前置与普通任务必须分别显示 Worker 状态，运行中的前置可停止。");
            var lastWorker = idle with { LastRun = run with { State = RunState.Succeeded } };
            var kinds = project.SelectedItems.Select(item => (item.Kind, item.Name)).ToArray();
            Require(RunProgress.Derive(lastWorker, kinds) is not null
                && RunProgress.Derive(lastWorker, kinds.Select(item => (PlanItemKind.MaaTask, item.Name))
                    .ToArray()) is null, "Last Run 匹配必须包含种类，不能只看任务名称。");

            Observe(idle);
            Click(ButtonNamed(Cards()[2], "从执行计划移除 Real task"));
            Require(project.SelectedTaskNames.Count == 0 && project.SelectedItemCount == 2
                && Named<Button>(window, "StartTaskHeaderButton").IsEnabled,
                "只选择前置任务时仍可开始，移除普通同名任务不得影响前置任务。");
            var onlyPre = project.CreateRunStartAttempt().Plan;
            var preItem = onlyPre.Items[0];
            var startingItem = new PlanItemSnapshot(preItem.PlanItemId, preItem.TaskName, preItem.TaskLabel,
                preItem.Entry, preItem.ResolvedOptions, preItem.PipelineOverride, PlanItemState.Starting,
                started, null, null, null, null) { Kind = PlanItemKind.Pretask };
            var starting = run with {
                State = RunState.Starting, CurrentPlanItemId = preItem.PlanItemId, CurrentPlanItemIndex = 0,
                Plan = onlyPre, Items = onlyPre.Items.Select((item, index) => index == 0 ? startingItem
                    : new PlanItemSnapshot(item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry,
                        item.ResolvedOptions, item.PipelineOverride, PlanItemState.Pending,
                        null, null, null, null, null) {
                        Kind = item.Kind
                    }).ToArray()
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
