using System.Windows.Media;
using System.Windows.Media.Imaging;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Views;

internal sealed record ProjectIdentity(string Name, string? IconPath);

public partial class MainWindow
{
    private const string ApplicationName = "NarutoAutoGUI";
    private (ImageSource? Window, ImageSource? TitleBar)? _defaultIcons;

    // The tray follows the project name and icon; IconPath is null while the default icon is shown.
    internal event EventHandler<ProjectIdentity>? ProjectIdentityChanged;

    // Telemetry for the loaded project after the release gate, with the user's current choice.
    private TelemetryOptions? TelemetryOptionsFor(ProjectPlanModule project) => TelemetryGate.CreateOptions(
        TelemetryBuild.Current, project.Telemetry, project.ProjectName, project.ProjectVersion,
        _settings.HelpImprove.Value);

    // Project title, icon and About, reapplied on every load; without a project the application's own are shown.
    private void ShowProjectIdentity(ProjectPlanModule? project)
    {
        Title = AppTitleBar.Title = project?.Title ?? ApplicationName;
        _defaultIcons ??= (Icon, AppTitleBarIcon.Source);
        string? path = null;
        ImageSource? icon = null;
        try {
            path = project?.ResolveIconPath();
            icon = path is null ? null
                : BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        } catch (Exception exception) {
            _logger.Warn($"加载项目图标失败，已使用默认图标。path={path}", exception);
            path = null;
        }
        Icon = icon ?? _defaultIcons.Value.Window;
        AppTitleBarIcon.Source = icon ?? _defaultIcons.Value.TitleBar;
        _settings.ShowProjectAbout(project?.About, icon);
        var telemetry = project is null ? null : TelemetryOptionsFor(project);
        _settings.ShowTelemetryAvailability(telemetry is not null);
        _telemetry.Configure(telemetry);
        ProjectIdentityChanged?.Invoke(this, new ProjectIdentity(project?.ProjectName ?? ApplicationName, path));
    }
}
