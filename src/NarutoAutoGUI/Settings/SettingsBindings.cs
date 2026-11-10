using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using NarutoAutoGUI.ProjectModel;

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

    protected void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

internal sealed class SettingsToggle(Action<bool> save) : SettingsObservable
{
    private bool _value = true;
    private bool _isEnabled = true;
    private string _status = "";

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

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

internal sealed record SettingsChoiceOption(string? Id, string Name);

// Picks one of a list that changes while the application runs. The first option is always "none", which has no Id.
internal sealed class SettingsChoice(string noneName, Action<string?> save) : SettingsObservable
{
    private readonly SettingsChoiceOption _none = new(null, noneName);
    private IReadOnlyList<SettingsChoiceOption>? _options;
    private SettingsChoiceOption? _selected;
    private string _status = "";

    public IReadOnlyList<SettingsChoiceOption> Options => _options ??= [_none];
    public string Status { get => _status; set => Set(ref _status, value); }

    public SettingsChoiceOption Selected
    {
        get => _selected ?? _none;
        set {
            // A ComboBox clears its selection while the options are swapped; that is not a choice.
            if (value is null || value == Selected) { return; }
            var previous = Selected;
            _selected = value;
            try {
                save(value.Id);
                Status = "";
            } catch (Exception) {
                _selected = previous;
                Status = "无法保存设置，已恢复原选择，请重试。";
            }
            Raise(nameof(Selected));
        }
    }

    // Reading the list must never write the choice back; a choice that is no longer listed falls back to none.
    internal void SetOptions(IEnumerable<SettingsChoiceOption> options, string? selectedId)
    {
        _options = [_none, .. options];
        _selected = _options.FirstOrDefault(option => option.Id is not null && option.Id == selectedId) ?? _none;
        Raise(nameof(Options));
        Raise(nameof(Selected));
    }
}

internal sealed class SettingsAction(Func<Task> execute) : SettingsObservable, ICommand
{
    private bool _isEnabled = true;
    private bool _isVisible = true;
    private string _status = "";
    private string? _toolTip;

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

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

internal sealed class ProjectAboutSettings : SettingsObservable
{
    private ProjectAbout? _content;
    private ImageSource? _icon;
    private Action<Uri> _openLink = _ => { };
    public ProjectAbout? Content { get => _content; set => Set(ref _content, value); }
    // Null shows the application's own icon.
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public Action<Uri> OpenLink { get => _openLink; set => Set(ref _openLink, value); }
}

internal sealed class SettingsRegistry
{
    internal ProjectAboutSettings About { get; } = new();
    internal Dictionary<string, SettingsToggle> Toggles { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SettingsChoice> Choices { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SettingsAction> Actions { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SettingsValue> Values { get; } = new(StringComparer.Ordinal);

    internal SettingsPageModel Bind(SettingsDefinition definition)
    {
        return new SettingsPageModel(definition, definition.Sections.Select(section =>
            new SettingsSectionModel(section, section.Items.Select(BindItem).ToArray())).ToArray());
    }

    private SettingsItemModel BindItem(SettingsItemDefinition item) => item.Type switch {
        SettingsItemKind.Toggle => new(item, Toggle: Resolve(Toggles, item.SettingKey!, item.Id)),
        SettingsItemKind.Choice => new(item, Choice: Resolve(Choices, item.ChoiceKey!, item.Id)),
        SettingsItemKind.Action => new(item, Action: Resolve(Actions, item.ActionId!, item.Id)),
        SettingsItemKind.Info => new(item,
            Value: item.ValueKey is null ? null : Resolve(Values, item.ValueKey, item.Id)),
        SettingsItemKind.About => new(item, About: About),
        _ => throw new InvalidDataException($"未知 Settings item type：{item.Type}。")
    };

    private static T Resolve<T>(Dictionary<string, T> registrations, string key, string itemId)
        => registrations.TryGetValue(key, out var binding) ? binding
            : throw new InvalidDataException($"Settings item {itemId} 引用了未注册的 key：{key}。");
}

internal sealed record SettingsPageModel(SettingsDefinition Definition, IReadOnlyList<SettingsSectionModel> Sections);
internal sealed record SettingsSectionModel(
    SettingsSectionDefinition Definition, IReadOnlyList<SettingsItemModel> Items)
{
    public IReadOnlyList<IReadOnlyList<SettingsItemModel>> Cards { get; } = GroupCards(Items);

    private static List<List<SettingsItemModel>> GroupCards(IReadOnlyList<SettingsItemModel> items)
    {
        var cards = new List<List<SettingsItemModel>>();
        foreach (var item in items) {
            if (!item.Definition.JoinPrevious) { cards.Add([]); }
            cards[^1].Add(item);
        }
        return cards;
    }
}

internal sealed record SettingsItemModel(SettingsItemDefinition Definition,
    SettingsToggle? Toggle = null, SettingsAction? Action = null, SettingsValue? Value = null,
    ProjectAboutSettings? About = null, SettingsChoice? Choice = null);
