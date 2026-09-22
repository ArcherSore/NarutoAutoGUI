using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NarutoAutoGUI.Settings;

internal abstract class SettingsObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value)) {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}

internal sealed class SettingsToggle(Action<bool> save) : SettingsObservable
{
    private bool _value = true;

    public bool Value
    {
        get => _value;
        set {
            if (_value == value) { return; }
            Set(ref _value, value);
            save(value);
        }
    }

    // Reading a preference must never write it back during startup or rendering.
    internal void Initialize(bool value) => Set(ref _value, value, nameof(Value));
}

internal sealed class SettingsAction(Func<Task> execute) : SettingsObservable, ICommand
{
    private bool _isEnabled = true;
    private string _status = "";
    private string? _toolTip;

    public bool IsEnabled
    {
        get => _isEnabled;
        set {
            if (_isEnabled == value) { return; }
            Set(ref _isEnabled, value);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public string? ToolTip { get => _toolTip; set => Set(ref _toolTip, value); }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => IsEnabled;
    public async void Execute(object? parameter) => await ExecuteAsync();
    internal Task ExecuteAsync() => IsEnabled ? execute() : Task.CompletedTask;
}

internal sealed class SettingsValue : SettingsObservable
{
    private string _text = "";
    public string Text { get => _text; set => Set(ref _text, value); }
}

internal sealed class SettingsRegistry
{
    internal Dictionary<string, SettingsToggle> Toggles { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SettingsAction> Actions { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SettingsValue> Values { get; } = new(StringComparer.Ordinal);

    internal SettingsPageModel Bind(SettingsDefinition definition)
    {
        return new SettingsPageModel(definition, definition.Sections.Select(section =>
            new SettingsSectionModel(section, section.Items.Select(BindItem).ToArray())).ToArray());
    }

    private SettingsItemModel BindItem(SettingsItemDefinition item) => item.Type switch {
        SettingsItemKind.Toggle => new(item, Toggle: Resolve(Toggles, item.SettingKey!, item.Id)),
        SettingsItemKind.Action => new(item, Action: Resolve(Actions, item.ActionId!, item.Id)),
        SettingsItemKind.Info => new(item,
            Value: item.ValueKey is null ? null : Resolve(Values, item.ValueKey, item.Id)),
        _ => throw new InvalidDataException($"未知 Settings item type：{item.Type}。")
    };

    private static T Resolve<T>(Dictionary<string, T> registrations, string key, string itemId)
        => registrations.TryGetValue(key, out var binding) ? binding
            : throw new InvalidDataException($"Settings item {itemId} 引用了未注册的 key：{key}。");
}

internal sealed record SettingsPageModel(SettingsDefinition Definition, IReadOnlyList<SettingsSectionModel> Sections);
internal sealed record SettingsSectionModel(
    SettingsSectionDefinition Definition, IReadOnlyList<SettingsItemModel> Items);

internal sealed record SettingsItemModel(SettingsItemDefinition Definition,
    SettingsToggle? Toggle = null, SettingsAction? Action = null, SettingsValue? Value = null)
{
    public string? Heading => Definition.Type == SettingsItemKind.Action && Definition.Title == Definition.ButtonText
        ? null : Definition.Title;
}
