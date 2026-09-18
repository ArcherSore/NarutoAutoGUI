using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Worker;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private bool _committingConfigurationInput;

    private bool CanEditConfiguration => _projectPlan is not null && !_busy && !_exitInProgress
        && _sessionSnapshot.State is not (ChildSessionState.Connecting or ChildSessionState.Disconnecting)
        && (_workerSnapshot.Observation is WorkerObservation.WorkerNotStarted or WorkerObservation.ChildSessionEnded
            || _workerSnapshot.Observation == WorkerObservation.Connected && _workerSnapshot.SnapshotFresh
            && RuntimeControlWorker is { ActiveRun: null, RunState: Protocol.RunState.Idle });

    private void RenderConfigurationTabs()
    {
        var project = _projectPlan!;
        var existing = ConfigurationTabs.Items.Cast<TabItem>().ToArray();
        if (!existing.Select(item => (Id: (Guid)item.Tag, Name: item.ToolTip as string))
            .SequenceEqual(project.Configurations.Select(item => (item.Id, (string?)item.Name)))) {
            ConfigurationTabs.Items.Clear();
            foreach (var configuration in project.Configurations) {
                var tab = new TabItem {
                    Tag = configuration.Id, ToolTip = configuration.Name, Padding = new Thickness(10, 6, 10, 6),
                    Header = new TextBlock {
                        Text = configuration.Name, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    ContextMenu = CreateConfigurationMenu(configuration.Id)
                };
                AutomationProperties.SetName(tab, configuration.Name);
                ConfigurationTabs.Items.Add(tab);
            }
        }
        var selected = ConfigurationTabs.Items.Cast<TabItem>()
            .Single(item => (Guid)item.Tag == project.ActiveConfigurationId);
        ConfigurationTabs.SelectedItem = selected;
        _ = Dispatcher.BeginInvoke(() => selected.BringIntoView());
    }

    private ContextMenu CreateConfigurationMenu(Guid id)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名", Tag = id };
        rename.Click += RenameConfiguration_Click;
        var delete = new MenuItem { Header = "删除配置", Tag = id };
        delete.Click += DeleteConfiguration_Click;
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Opened += (_, _) => {
            rename.IsEnabled = CanEditConfiguration;
            delete.IsEnabled = CanEditConfiguration && _projectPlan!.Configurations.Count > 1;
        };
        return menu;
    }

    private void ConfigurationMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditConfiguration || !CommitFocusedConfigurationInput() || sender is not WpfButton button) {
            return;
        }
        button.ContextMenu = CreateConfigurationMenu(_projectPlan!.ActiveConfigurationId);
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void ConfigurationActions_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CommitFocusedConfigurationInput()) {
            e.Handled = true;
        }
    }

    private void ConfigurationActions_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CommitFocusedConfigurationInput()) {
            e.Handled = true;
        }
    }

    private bool CommitFocusedConfigurationInput()
    {
        if (Keyboard.FocusedElement is not WpfTextBox editor) {
            return true;
        }
        return editor.Tag is OptionInputTag ? CommitOptionInput(editor)
            : editor == ConfigurationNameEditor ? CommitConfigurationName() : true;
    }

    private void NewConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeConfiguration(() => _projectPlan!.CreateConfiguration());
    }

    private void ConfigurationTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOptionEditors || e.Source != ConfigurationTabs
            || ConfigurationTabs.SelectedItem is not TabItem { Tag: Guid id }) {
            return;
        }
        ChangeConfiguration(() => _projectPlan!.ActivateConfiguration(id));
    }

    private void DeleteConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Guid id }) {
            ChangeConfiguration(() => _projectPlan!.DeleteConfiguration(id));
        }
    }

    private void ChangeConfiguration(Action change)
    {
        if (!CanEditConfiguration || !CommitFocusedConfigurationInput()) {
            if (_projectPlan is not null) {
                RestoreConfigurationSelection();
            }
            return;
        }
        try {
            change();
            _pendingStartAttempt = null;
            _expandedTaskName = null;
            _dragTaskName = null;
            ConfigurationNameEditor.Tag = null;
            ConfigurationNamePanel.Visibility = Visibility.Collapsed;
            CloseTaskDescriptionDrawer();
            RenderTaskPlan();
        } catch (Exception exception) {
            RestoreConfigurationSelection();
            HandleProjectEditError("保存任务配置失败", exception);
        }
    }

    private void RestoreConfigurationSelection()
    {
        _updatingOptionEditors = true;
        try {
            RenderConfigurationTabs();
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private void RenameConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditConfiguration || !CommitFocusedConfigurationInput()
            || sender is not MenuItem { Tag: Guid id }) {
            return;
        }
        var configuration = _projectPlan!.Configurations.Single(item => item.Id == id);
        ConfigurationNameEditor.Tag = id;
        ConfigurationNameEditor.Text = configuration.Name;
        ConfigurationNamePanel.Visibility = Visibility.Visible;
        ConfigurationNameEditor.Focus();
        ConfigurationNameEditor.SelectAll();
    }

    private void ConfigurationNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_updatingOptionEditors) {
            _ = CommitConfigurationName();
        }
    }

    private void ConfigurationNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) {
            _ = CommitConfigurationName();
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            ConfigurationNameEditor.Tag = null;
            ConfigurationNamePanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private bool CommitConfigurationName()
    {
        if (!CanEditConfiguration || ConfigurationNameEditor.Tag is not Guid id || _committingConfigurationInput) {
            return true;
        }
        _committingConfigurationInput = true;
        try {
            _projectPlan!.RenameConfiguration(id, ConfigurationNameEditor.Text);
            ConfigurationNameEditor.Tag = null;
            ConfigurationNamePanel.Visibility = Visibility.Collapsed;
            TryRenderTaskPlan();
            return true;
        } catch (Exception exception) {
            ConfigurationNameEditor.Text = _projectPlan!.Configurations.Single(item => item.Id == id).Name;
            HandleOperationError("保存配置名称失败", exception);
            return false;
        } finally {
            _committingConfigurationInput = false;
        }
    }

    private bool CommitOptionInput(WpfTextBox textBox)
    {
        if (_updatingOptionEditors || _committingConfigurationInput || !CanEditConfiguration
            || textBox.Tag is not OptionInputTag tag || tag.Submitted && textBox.Text == tag.Value) {
            return true;
        }
        _committingConfigurationInput = true;
        try {
            _projectPlan!.SetInputValue(tag.ConfigurationId, tag.OptionName, tag.InputName, textBox.Text);
            textBox.Tag = tag with { Value = textBox.Text, Submitted = true };
            _pendingStartAttempt = null;
            TryRenderTaskPlan();
            return true;
        } catch (Exception exception) {
            textBox.Text = tag.Value;
            textBox.Tag = tag with { Submitted = true };
            HandleOperationError("保存 MaaNOP input option 失败", exception);
            ShowProjectValidationError(exception);
            return exception is InvalidDataException;
        } finally {
            _committingConfigurationInput = false;
        }
    }
}
