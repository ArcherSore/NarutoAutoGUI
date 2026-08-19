namespace NarutoAutoGUI.Models;

internal sealed class AppSettings
{
    internal const int CurrentSchemaVersion = 2;

    internal const string DefaultGameExecutablePath =
        @"C:\Users\17321\AppData\Roaming\Tencent\QQMicroGameBox\Launch.exe";

    internal const string DefaultGameArguments = "-/appid:1103286479";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string GameExecutablePath { get; set; } = DefaultGameExecutablePath;

    public string GameArguments { get; set; } = DefaultGameArguments;

    public string MaaNopProjectDirectory { get; set; } = string.Empty;
}
