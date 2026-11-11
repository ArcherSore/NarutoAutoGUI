using Protocol;

namespace Worker;

internal sealed class MaaRunLogAdapter
{
    private readonly Action<string, string, string> _log;
    private int _warningLogged;

    internal MaaRunLogAdapter(Action<string, string, string> log)
    {
        _log = log;
    }

    // Writes the callback's focus content to the run log and returns the focus, so it is parsed once per callback.
    internal MaaFocus Handle(string message, string detailsJson)
    {
        try {
            var focus = MaaRunLogFormatter.Read(message, detailsJson);
            if (focus.LogText is not null) {
                _log("INFO", ProtocolConstants.RunLogSource, focus.LogText);
            }
            return focus;
        } catch (Exception exception) {
            if (Interlocked.Exchange(ref _warningLogged, 1) != 0) {
                return default;
            }

            try {
                _log(
                    "WARN", "framework.callback",
                    $"MaaFramework Callback focus 解析失败：{exception.GetBaseException().Message}");
            } catch {
                // A logging failure must never escape the MaaFramework callback thread.
            }
            return default;
        }
    }
}
