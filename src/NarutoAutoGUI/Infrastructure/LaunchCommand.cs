namespace NarutoAutoGUI.Infrastructure;

// What this launch was asked to do. Like MXU and MFA, unrecognized arguments are ignored.
internal sealed record LaunchCommand(bool AutoStart, bool QuitAfterRun, string? Instance)
{
    internal static LaunchCommand Parse(IReadOnlyList<string> args)
    {
        var autoStart = false;
        var quit = false;
        string? instance = null;
        for (var index = 0; index < args.Count; index++) {
            var (name, value) = Split(args[index]);
            switch (name.ToLowerInvariant()) {
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
        return new LaunchCommand(autoStart, quit, instance);
    }

    private static (string Name, string? Value) Split(string argument)
    {
        var equals = argument.IndexOf('=');
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }
}
