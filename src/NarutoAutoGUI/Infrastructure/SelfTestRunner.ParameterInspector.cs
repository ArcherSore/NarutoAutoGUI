using System.Text.Json.Nodes;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyParameterInspector(string testDirectory, string projectDirectory)
    {
        var none = new Dictionary<(string, string), InputDraft>();
        var simple = ProjectPlanModule.Open(projectDirectory, Path.Combine(testDirectory, "inspector-simple.json"));
        var rows = ParameterInspector.Derive(simple.GetConfiguration("RealTask"), none, []);
        Require(rows.Select(row => (row.Option.Name, row.Depth, row.IsGlobal)).SequenceEqual(new[] {
            ("ServerRange", 0, true), ("Mode", 0, false), ("Nested", 1, false)
        }), "检查器行必须先全局后任务，按声明顺序深度优先。");
        var server = rows[0];
        Require(server.Kind == ProjectOptionKind.Input && server.Label == "Server range"
            && server.Inputs.Select(input => input.Label).SequenceEqual(new[] { "Server", "Retry count", "Enabled" })
            && server.Inputs[0].AccessibleName == "Server range · Server"
            && server.Description.StartsWith("A long option description", StringComparison.Ordinal),
            "多输入选项必须归为一行，并保留选项标签、说明和各输入标签。");
        Require(rows[1].Kind == ProjectOptionKind.Select && rows[1].Label == "Mode"
            && rows[2].Kind == ProjectOptionKind.Switch && rows[2].Label == "Nested",
            "无 label 的 select/switch 行应回退到选项名。");
        Require(rows.All(row => row.DefaultHint is null && row.Inputs.All(input => input.DefaultHint is null)),
            "跟随默认的值不显示默认提示。");

        var directory = CreateComplexInspectorFixture(testDirectory);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        project.AddTask("StressTask");
        project.AddTask("ThirdTask");
        var others = new[] {
            ("Real task", project.GetConfiguration("RealTask")), ("Third task", project.GetConfiguration("ThirdTask"))
        };
        rows = ParameterInspector.Derive(project.GetConfiguration("StressTask"), none, others);
        Require(rows.Select(row => (row.Option.Name, row.Depth)).SequenceEqual(new[] {
            ("ServerRange", 0), ("Mode", 0), ("Nested", 1), ("Level1", 0), ("Level2", 1), ("Level3", 2)
        }), "选中 case 的子选项必须紧跟父行，三级嵌套深度依次递增。");
        Require(rows[3].Label == "Level1" && rows[5].Label == "Keyword" && rows[5].Inputs.Single().Label == "Keyword",
            "标签回退必须为选项 label、第一个输入 label、选项名。");
        Require(rows[3].CaseDescription == "Goes one level deeper" && rows[1].CaseDescription is null,
            "选中 case 有说明时才给出 case 说明。");
        Require(rows[0].IsGlobal && rows[0].SharedWith.Count == 0
            && rows[1].SharedWith.SequenceEqual(new[] { "Real task", "Third task" })
            && rows[2].SharedWith.SequenceEqual(new[] { "Real task", "Third task" })
            && rows[3].SharedWith.Count == 0 && rows[5].SharedWith.Count == 0,
            "共用提示必须列出执行计划中使用同一选项的其他任务，全局选项另行标记。");

        project.SetInputValue("ServerRange", "retry_count", "3");
        project.SetInputValue("ServerRange", "server_range", "979");
        project.SetSelectedCase("Mode", "Default");
        var drafts = new Dictionary<(string, string), InputDraft> {
            [("ServerRange", "enabled")] = new("maybe", "必须是 true 或 false")
        };
        rows = ParameterInspector.Derive(project.GetConfiguration("StressTask"), drafts, others);
        var inputs = rows[0].Inputs;
        Require(inputs[0].DefaultHint == "默认：978-1012" && inputs[1].DefaultHint is null
            && rows[1].DefaultHint is null, "默认提示只在显式值与默认值不同时出现。");
        Require(rows[0].IsInvalid && inputs[2].Draft?.Text == "maybe" && inputs[2].SavedValue == "true"
            && inputs[2].DisplayValue == "maybe" && inputs[2].DefaultHint is null && !rows[1].IsInvalid,
            "草稿状态必须给出草稿文本、提示与已保存值。");

        project.SetSelectedCase("Level1", "Shallow");
        rows = ParameterInspector.Derive(project.GetConfiguration("StressTask"), none, others);
        Require(rows.Select(row => row.Option.Name).SequenceEqual(new[] {
            "ServerRange", "Mode", "Nested", "Level1"
        }) && rows[3].DefaultHint == "默认：Deep" && rows[3].CaseDescription is null,
            "切换 case 后子行必须消失，并提示默认 case。");
        project.SetSelectedCase("Level1", "Deep");
        rows = ParameterInspector.Derive(project.GetConfiguration("StressTask"), none, others);
        Require(rows.Count == 6 && rows[3].DefaultHint is null, "切回默认 case 后子行恢复且不再提示默认值。");
    }

    private static void VerifyPlanItemHeaders(AppLogger logger, string testDirectory)
    {
        var directory = CreateComplexInspectorFixture(Path.Combine(testDirectory, "plan-headers"));
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        var plain = json["task"]![0]!.DeepClone();
        plain["name"] = "PlainTask";
        plain["label"] = "Plain task";
        plain["option"] = new JsonArray();
        json["global_option"] = new JsonArray();
        json["task"]!.AsArray().Add(plain);
        File.WriteAllText(interfacePath, json.ToJsonString());
        using var session = new ChildSession.ChildSessionManager(logger);
        var coordinator = new Worker.WorkerCoordinator(logger, Path.Combine(testDirectory, "plan-headers-state"),
            "unused.exe", $"NarutoAutoGUI.PlanHeaders.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
        var window = new MainWindow(logger, session, coordinator, WindowEnvironment(logger, session, coordinator),
            () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask);
        try {
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
            project.AddTask("StressTask");
            project.AddTask("PlainTask");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            SetOnboardingField(window, "_projectPlan", project);
            SetOnboardingField(window, "_expandedTaskName", null!);
            InvokeOnboarding(window, "RenderTaskPlan");
            string NameOf(System.Windows.DependencyObject element) =>
                System.Windows.Automation.AutomationProperties.GetName(element);
            System.Windows.Controls.Button[] Buttons() => ConfigurationDescendants(items)
                .OfType<System.Windows.Controls.Button>().ToArray();
            void Click(string name) => Buttons().First(button => NameOf(button) == name).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            string[] Expanded() => Buttons().Select(NameOf)
                .Where(name => name.EndsWith("，已展开", StringComparison.Ordinal)).ToArray();
            int InspectorRows() => ConfigurationDescendants(items).OfType<ParameterRowPanel>().Count();

            Require(Expanded().Length == 0 && InspectorRows() == 0 && Buttons().All(button => NameOf(button) is not
                ("全部折叠" or "展开参数" or "收起参数")), "执行计划中不应有全部折叠或独立的展开/收起按钮。");
            Click("Real task，已折叠");
            Require(Expanded().SequenceEqual(new[] { "Real task，已展开" }) && InspectorRows() == 2,
                "点击标题区必须展开该卡片的参数检查器。");
            Click("Stress task，已折叠");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" }) && InspectorRows() == 5,
                "展开另一张卡片时必须收起之前的卡片。");
            Click("拖动以调整顺序");
            Click("从执行计划移除 Plain task");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" })
                && !project.SelectedTaskNames.Contains("PlainTask"), "拖动手柄与移除按钮不得切换展开。");
            project.AddTask("PlainTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            Click("Plain task，没有可配置的参数");
            Require(Expanded().SequenceEqual(new[] { "Stress task，已展开" }), "无参数任务的标题区不得展开空白区域。");
            Click("查看 Stress task 的任务描述");
            var drawer = (System.Windows.UIElement)window.FindName("TaskDescriptionOverlay");
            Require(drawer.Visibility == System.Windows.Visibility.Visible
                && Expanded().SequenceEqual(new[] { "Stress task，已展开" }), "任务说明按钮只打开说明，不切换展开。");
            InvokeOnboarding(window, "CloseTaskDescriptionDrawer");
            Click("Stress task，已展开");
            Require(Expanded().Length == 0 && InspectorRows() == 0, "再次点击已展开的标题区必须收起。");

            Click("Stress task，已折叠");
            SetOnboardingField(window, "_pendingOperation", PendingOperation.PreparingEnvironment);
            InvokeOnboarding(window, "RefreshRuntimeControls");
            var rows = ConfigurationDescendants(items).OfType<ParameterRowPanel>().ToArray();
            var editors = rows.SelectMany(ConfigurationDescendants).OfType<System.Windows.Controls.Control>()
                .Where(control => control is System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox
                    or Wpf.Ui.Controls.ToggleSwitch).ToArray();
            var notes = rows.SelectMany(ConfigurationDescendants).OfType<System.Windows.Controls.TextBlock>()
                .Where(text => text.Text == "Goes one level deeper").ToArray();
            Require(editors.Length == 5 && editors.All(editor => !editor.IsEnabled) && notes.Length == 1
                && notes.All(note => note.IsEnabled), "锁定时参数控件禁用，说明保持可读。");
        } finally {
            window.AllowClose();
            window.Close();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    // The inspector at the three reference window widths: stacked when narrow, two columns otherwise, never
    // scrolling sideways. Set NARUTO_TASK_SCREENSHOTS to keep the rendered windows for a visual check.
    private static void VerifyParameterInspectorWindow(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "inspector-window"), (window, _) => {
            var project = (ProjectPlanModule)typeof(MainWindow).GetField("_projectPlan",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            project.AddTask("StressTask");
            project.AddTask("ThirdTask");
            typeof(MainWindow).GetMethod("EndOnboarding", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { true });
            SetOnboardingField(window, "_expandedTaskName", "StressTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            var planScroll = (System.Windows.Controls.ScrollViewer)window.FindName("PlanScroll");
            var screenshots = Environment.GetEnvironmentVariable("NARUTO_TASK_SCREENSHOTS");
            foreach (var (width, stacked) in new[] { (920, true), (1180, false), (1440, false) }) {
                window.Width = width;
                window.Height = width == 920 ? 640 : width == 1180 ? 760 : 900;
                PumpOnboarding();
                var rows = ConfigurationDescendants(items).OfType<ParameterRowPanel>().ToArray();
                Require(rows.Length == 6 && rows.All(row => row.IsStacked == stacked)
                    && planScroll.ComputedHorizontalScrollBarVisibility != System.Windows.Visibility.Visible,
                    $"{width} 宽度下参数检查器应{(stacked ? "上下堆叠" : "为两列")}，且没有横向滚动。");
                if (!string.IsNullOrEmpty(screenshots)) {
                    Directory.CreateDirectory(screenshots);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,
                        (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(screenshots, $"inspector-{width}.png"));
                    encoder.Save(stream);
                }
            }
        }, AddComplexInspectorOptions);
    }

    private static void VerifyParameterRowLayout()
    {
        Require(ParameterRowPanel.CalculateLabelWidth(469) is null && ParameterRowPanel.CalculateLabelWidth(470) == 169
            && ParameterRowPanel.CalculateLabelWidth(600) == 216 && ParameterRowPanel.CalculateLabelWidth(1200) == 280,
            "标签列约占编辑区 36%，限制在 140–280，窄于 470 时上下堆叠。");
        Require(ParameterInputsPanel.CalculateColumns(140, 3) == 1 && ParameterInputsPanel.CalculateColumns(300, 3) == 2
            && ParameterInputsPanel.CalculateColumns(900, 3) == 3, "多输入在宽度不足时换行。");

        var description = new System.Windows.Controls.TextBlock {
            Text = string.Concat(Enumerable.Repeat("A long description that must wrap. ", 12)),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        var value = new System.Windows.Controls.Border { Height = 32 };
        var row = new ParameterRowPanel { Depth = 1, Children = { description, value } };
        System.Windows.Rect Arrange(double width)
        {
            row.Measure(new System.Windows.Size(width, double.PositiveInfinity));
            row.Arrange(new System.Windows.Rect(0, 0, width, row.DesiredSize.Height));
            return System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(value);
        }
        System.Windows.Rect LabelSlot() =>
            System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(description);
        var wide = Arrange(600);
        Require(!row.IsStacked && wide.X == 217 && wide.Width == 383 && LabelSlot().Width == 216
            && description.ActualHeight > 40, "宽编辑区为两列，长说明在标签列内换行，没有横向溢出。");
        var narrow = Arrange(400);
        Require(row.IsStacked && narrow.X == ParameterRowPanel.IndentStep + 2
            && narrow.Y >= description.ActualHeight - 10 && LabelSlot().Width == 400, "窄编辑区标签与控件上下堆叠，控件按深度缩进。");
    }

    private static string CreateComplexInspectorFixture(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "inspector-complex"));
        AddComplexInspectorOptions(directory);
        return directory;
    }

    // Adds a task with multi-level nesting, a described case and blank labels, and one sharing an option.
    private static void AddComplexInspectorOptions(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        var tasks = json["task"]!.AsArray();
        foreach (var (name, label, options) in new[] {
            ("StressTask", "Stress task", new[] { "Mode", "Level1" }), ("ThirdTask", "Third task", new[] { "Mode" })
        }) {
            var task = tasks[0]!.DeepClone();
            task["name"] = name;
            task["label"] = label;
            task["option"] = new JsonArray(options.Select(option => (JsonNode)option).ToArray());
            tasks.Add(task);
        }
        var definitions = json["option"]!.AsObject();
        definitions["Level1"] = JsonNode.Parse("""
            {"type": "select", "label": "", "default_case": "Deep", "cases": [
              {"name": "Deep", "description": "Goes one level deeper", "option": ["Level2"]},
              {"name": "Shallow"}]}
            """);
        definitions["Level2"] = JsonNode.Parse("""
            {"type": "switch", "default_case": "Yes", "cases": [
              {"name": "Yes", "option": ["Level3"]}, {"name": "No"}]}
            """);
        definitions["Level3"] = JsonNode.Parse("""
            {"type": "input", "label": "", "inputs": [
              {"name": "keyword", "label": "Keyword", "default": "", "pipeline_type": "string"}],
             "pipeline_override": {"Keyword": {"text": "{keyword}"}}}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
