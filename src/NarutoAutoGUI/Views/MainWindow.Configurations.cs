using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Worker;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private bool _committingConfigurationInput;
    private WpfTextBox? _configurationNameEditor;

    private bool CanEditConfiguration => _projectPlan is not null && !_busy && !_exitInProgress
        && _sessionSnapshot.State is not (ChildSessionState.Connecting or ChildSessionState.Disconnecting)
        && (_workerSnapshot.Observation is WorkerObservation.WorkerNotStarted or WorkerObservation.ChildSessionEnded
            || _workerSnapshot.Observation == WorkerObservation.Connected && _workerSnapshot.SnapshotFresh
            && RuntimeControlWorker is { ActiveRun: null, RunState: Protocol.RunState.Idle });

    private void RenderConfigurationTabs()
    {
        var project = _projectPlan!;
        var existing = ConfigurationTabs.Items.Cast<TabItem>().ToArray();
        if (!existing.Select(item => (Id: (Guid)item.Tag, Name: AutomationProperties.GetName(item)))
            .SequenceEqual(project.Configurations.Select(item => (item.Id, item.Name)))) {
            ConfigurationTabs.Items.Clear();
            foreach (var configuration in project.Configurations) {
                var header = new Grid();
                var label = new TextBlock {
                    Text = configuration.Name, MaxWidth = 150, Margin = new Thickness(0, 0, 16, 0),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                };
                header.Children.Add(label);
                var delete = new WpfButton {
                    Tag = configuration.Id, Style = (Style)FindResource("Configuration.Delete"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    IsEnabled = project.Configurations.Count > 1,
                    Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss16,
                        FontSize = 10 }
                };
                AutomationProperties.SetName(delete, $"删除配置 {configuration.Name}");
                delete.PreviewMouseLeftButtonDown += (_, e) => {
                    if (!CanEditConfiguration || _configurationNameEditor is not null
                        || !CommitFocusedConfigurationInput()) {
                        e.Handled = true;
                    } else {
                        delete.Focus();
                        delete.CaptureMouse();
                        e.Handled = true;
                    }
                };
                delete.PreviewMouseLeftButtonUp += (_, e) => {
                    if (delete.IsMouseCaptured) {
                        delete.ReleaseMouseCapture();
                        if (delete.IsMouseOver) {
                            DeleteConfiguration_Click(delete, e);
                        }
                        e.Handled = true;
                    }
                };
                delete.Click += DeleteConfiguration_Click;
                header.Children.Add(delete);
                var tab = new TabItem {
                    Tag = configuration.Id, Header = header,
                    Style = (Style)FindResource("Configuration.Tab")
                };
                label.SetBinding(TextBlock.FontWeightProperty,
                    new System.Windows.Data.Binding(nameof(FontWeight)) { Source = tab });
                label.SetBinding(TextBlock.ForegroundProperty,
                    new System.Windows.Data.Binding(nameof(Foreground)) { Source = tab });
                tab.MouseDoubleClick += (_, e) => {
                    if (e.ChangedButton == MouseButton.Left && !delete.IsMouseOver) {
                        BeginConfigurationRename(tab);
                        e.Handled = true;
                    }
                };
                tab.KeyDown += (_, e) => {
                    if (e.Key == Key.F2) {
                        BeginConfigurationRename(tab);
                        e.Handled = true;
                    }
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

    private void ConfigurationScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer { ScrollableWidth: > 0 } scroll && CanEditConfiguration) {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 120.0 * 64);
            e.Handled = true;
        }
    }

    private void ConfigurationActions_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_configurationNameEditor is not null) {
            if (!_configurationNameEditor.IsMouseOver) {
                _ = CommitConfigurationName();
                e.Handled = true;
            }
        } else if (!CommitFocusedConfigurationInput()) {
            e.Handled = true;
        }
    }

    private void ConfigurationActions_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_configurationNameEditor is not null) {
            return;
        }
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
            : editor == _configurationNameEditor ? CommitConfigurationName() : true;
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
        if (sender is WpfButton { Tag: Guid id }) {
            ChangeConfiguration(() => _projectPlan!.DeleteConfiguration(id));
        }
    }

    private void ChangeConfiguration(Action change)
    {
        if (!CanEditConfiguration || _configurationNameEditor is not null || !CommitFocusedConfigurationInput()) {
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
            EndConfigurationRename();
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

    private void BeginConfigurationRename(TabItem tab)
    {
        if (!CanEditConfiguration || _configurationNameEditor is not null || !CommitFocusedConfigurationInput()) {
            return;
        }
        var header = (Grid)tab.Header;
        var label = (TextBlock)header.Children[0];
        var editor = new WpfTextBox {
            Tag = (Guid)tab.Tag, Text = label.Text, MinWidth = 40, MaxWidth = 150,
            FontSize = label.FontSize, FontWeight = label.FontWeight,
            Height = 30, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Margin = label.Margin, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(editor, "配置名称");
        editor.LostKeyboardFocus += ConfigurationNameEditor_LostKeyboardFocus;
        editor.KeyDown += ConfigurationNameEditor_KeyDown;
        _configurationNameEditor = editor;
        label.Visibility = Visibility.Hidden;
        header.Children.Add(editor);
        editor.Focus();
        editor.SelectAll();
    }

    private void EndConfigurationRename()
    {
        if (_configurationNameEditor is not { Parent: Grid header } editor) {
            return;
        }
        _configurationNameEditor = null;
        editor.Tag = null;
        header.Children.Remove(editor);
        header.Children[0].Visibility = Visibility.Visible;
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
            EndConfigurationRename();
            ConfigurationTabs.Focus();
            e.Handled = true;
        }
    }

    private bool CommitConfigurationName()
    {
        if (!CanEditConfiguration || _configurationNameEditor is not { Tag: Guid id } editor
            || _committingConfigurationInput) {
            return true;
        }
        _committingConfigurationInput = true;
        try {
            if (!string.IsNullOrWhiteSpace(editor.Text)) {
                _projectPlan!.RenameConfiguration(id, editor.Text);
            }
            EndConfigurationRename();
            RestoreConfigurationSelection();
            return true;
        } catch (Exception exception) {
            editor.Text = _projectPlan!.Configurations.Single(item => item.Id == id).Name;
            EndConfigurationRename();
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
