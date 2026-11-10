using System.Text;

namespace NarutoAutoGUI.Infrastructure;

// What this launch was asked to do. Like MXU and MFA, unrecognized arguments are ignored.
internal sealed record LaunchCommand(bool AutoStart, bool QuitAfterRun, string? Instance, bool Help = false)
{
    internal const string HelpText = """
        NarutoAutoGUI 命令行参数

        用法:
          NarutoAutoGUI.exe [参数]

        参数:
          -h, --help
              显示本帮助并退出

          --autostart
              按开机自启动处理：窗口留在托盘，并自动执行配置
              通常由“开机自启动”创建的计划任务自动传入

          -i, --instance <配置名称或配置 ID>
              指定自动执行的配置；仅在 --autostart 时生效
              也支持 -i=<值> 与 --instance=<值>

          -q, --quit-after-run
              本次启动实际触发自动执行后，在任务结束时退出程序

        示例:
          NarutoAutoGUI.exe --autostart --instance "日常任务"
          NarutoAutoGUI.exe --autostart -i "日常任务" --quit-after-run

        """;

    internal static LaunchCommand Parse(IReadOnlyList<string> args)
    {
        var autoStart = false;
        var quit = false;
        var help = false;
        string? instance = null;
        for (var index = 0; index < args.Count; index++) {
            var (name, value) = Split(args[index]);
            switch (name.ToLowerInvariant()) {
                case "-h" or "--help":
                    help = true;
                    break;
                case "--autostart":
                    autoStart = true;
                    break;
                case "-q" or "--quit-after-run":
                    quit = true;
                    break;
                case "-i" or "--instance":
                    // A following option is never taken as the value.
                    if (value is null && index + 1 < args.Count && !args[index + 1].StartsWith('-')) {
                        value = args[++index];
                    }
                    instance = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
            }
        }
        return new LaunchCommand(autoStart, quit, instance, help);
    }

    // Arguments as one command line that Windows splits back into the same list (the CRT quoting rules).
    internal static string JoinArguments(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) {
            return argument;
        }
        var quoted = new StringBuilder("\"");
        for (var index = 0; ; index++) {
            var backslashes = 0;
            for (; index < argument.Length && argument[index] == '\\'; index++) {
                backslashes++;
            }
            if (index == argument.Length) {
                // Backslashes before the closing quote must not escape it.
                quoted.Append('\\', backslashes * 2);
                break;
            }
            if (argument[index] == '"') {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            } else {
                quoted.Append('\\', backslashes).Append(argument[index]);
            }
        }
        return quoted.Append('"').ToString();
    }

    private static (string Name, string? Value) Split(string argument)
    {
        var equals = argument.IndexOf('=');
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }
}
