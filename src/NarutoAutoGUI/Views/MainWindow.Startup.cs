using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private Button? _startupOnlyButton;
    private bool _pendingStartupOnly;

    private void RenderStartupStage(ProjectPlanModule project)
    {
        _startupOnlyButton = null;
        var pretasks = project.Pretasks.Concat(project.SelectedItems.Where(item => item.Kind == PlanItemKind.Pretask
            && !project.Pretasks.Any(available => available.Name == item.Name))).ToArray();
        if (pretasks.Length == 0) {
            return;
        }
        var body = new StackPanel();
        var stage = new Border {
            Background = (Brush)FindResource("Brush.Surface"),
            BorderBrush = (Brush)FindResource("Brush.Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body, ClipToBounds = true
        };
        AutomationProperties.SetName(stage, "启动阶段");
        var heading = new Grid { Height = 40, Margin = new Thickness(12, 0, 6, 0) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        caption.Children.Add(new Wpf.Ui.Controls.SymbolIcon {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Rocket16, FontSize = 16,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"), VerticalAlignment = VerticalAlignment.Center
        });
        caption.Children.Add(new TextBlock {
            Text = "启动阶段", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        caption.Children.Add(new TextBlock {
            Text = "连接游戏窗口前执行", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Brush.Text.Muted")
        });
        heading.Children.Add(caption);
        _startupOnlyButton = CreateIconButton(Wpf.Ui.Controls.SymbolRegular.Play16, "仅执行启动", "仅执行启动");
        _startupOnlyButton.Style = (Style)FindResource("StartupOnlyButtonStyle");
        if (_startupOnlyButton.Content is Wpf.Ui.Controls.SymbolIcon playIcon) {
            playIcon.Width = playIcon.Height = playIcon.FontSize = 18;
        }
        _startupOnlyButton.ClearValue(WidthProperty);
        _startupOnlyButton.ClearValue(HeightProperty);
        _startupOnlyButton.ClearValue(MinWidthProperty);
        _startupOnlyButton.Click += RunStartupOnly_Click;
        Grid.SetColumn(_startupOnlyButton, 1);
        heading.Children.Add(_startupOnlyButton);
        body.Children.Add(new Border {
            Background = (Brush)FindResource("Brush.Surface.Option"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"), BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(8, 8, 0, 0), Child = heading
        });
        foreach (var task in pretasks) {
            var card = CreatePlanItem(task, IsExpanded(task));
            card.BorderThickness = new Thickness(0);
            card.Margin = new Thickness(0);
            card.CornerRadius = new CornerRadius(0, 0, 8, 8);
            _planItemContainers.Add((task.Kind, task.Name), card);
            body.Children.Add(card);
        }
        PlanItemsPanel.Children.Add(stage);
        var divider = new DockPanel { Margin = new Thickness(12, 14, 2, 12), LastChildFill = true };
        divider.Children.Add(new Wpf.Ui.Controls.SymbolIcon {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Window16, FontSize = 14, Margin = new Thickness(0, 0, 8, 0),
            Foreground = (Brush)FindResource("Brush.Text.Muted")
        });
        divider.Children.Add(new TextBlock {
            Text = project.EnabledItems.Any(item => item.Kind == PlanItemKind.Pretask)
                ? "连接游戏窗口" : "连接游戏窗口（启动阶段已关闭，请先手动打开游戏）",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Text.Secondary")
        });
        divider.Children.Add(new Border {
            Height = 1, Margin = new Thickness(8, 0, 0, 0),
            Background = (Brush)FindResource("Brush.Border.Subtle"), VerticalAlignment = VerticalAlignment.Center
        });
        PlanItemsPanel.Children.Add(divider);
    }

    private CheckBox CreateStartupSelection(ProjectTaskChoice task)
    {
        var project = RequireProject();
        var selection = new CheckBox {
            Style = (Style)FindResource("StartupSwitchStyle"),
            IsChecked = project.SelectedPretaskNames.Contains(task.Name)
                && project.IsItemEnabled(task.Kind, task.Name), ToolTip = "是否在此配置中执行"
        };
        AutomationProperties.SetName(selection, $"执行 {task.Label}");
        BindConfigurationEditable(selection);
        selection.Click += (_, _) => {
            if (!CurrentControls.ConfigurationEditable) {
                return;
            }
            try {
                if (selection.IsChecked == true) {
                    project.AddPretask(task.Name);
                    project.SetItemEnabled(task.Kind, task.Name, true);
                } else {
                    project.RemovePretask(task.Name);
                }
                _pendingStartAttempt = null;
                RenderTaskPlan();
            } catch (Exception exception) {
                HandleProjectEditError("更新启动阶段失败", exception);
            }
        };
        Grid.SetColumn(selection, 1);
        return selection;
    }

    private async void RunStartupOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_startupOnlyButton?.IsEnabled != true || !CommitFocusedConfigurationInput()
            || RevealInvalidInputDraft() || OperationOrExitInProgress) {
            return;
        }
        if (CurrentControls.Action is RuntimeAction.Prepare
            or RuntimeAction.Retry { Target: RetryTarget.Prepare }) {
            await PrepareFromPanelAsync(startRun: true, startupOnly: true);
        } else if (CurrentControls.Action is RuntimeAction.Start
            or RuntimeAction.Retry { Target: RetryTarget.Start }) {
            await RunOperationAsync(PendingOperation.StartingRun, () => StartCurrentPlanAsync(startupOnly: true));
        }
    }

    private void RefreshStartupOnlyButton(RuntimeControlState state)
    {
        if (_startupOnlyButton is null || _projectPlan is not { } project) {
            return;
        }
        var enabled = project.EnabledItems.Any(item => item.Kind == PlanItemKind.Pretask);
        _startupOnlyButton.IsEnabled = state.ConfigurationEditable && enabled && _projectConfigurationValid
            && state.Action is RuntimeAction.Prepare { Enabled: true }
                or RuntimeAction.Start { Enabled: true } or RuntimeAction.Retry { Enabled: true };
    }
}
