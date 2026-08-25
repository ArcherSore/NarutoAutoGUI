using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed class MaaRunLogAdapter
{
    private readonly Action<string, string, string> _log;
    private int _warningLogged;

    internal MaaRunLogAdapter(Action<string, string, string> log)
    {
        _log = log;
    }

    internal void Handle(string message, string detailsJson)
    {
        try
        {
            var rendered = MaaRunLogFormatter.Format(message, detailsJson);
            if (rendered is not null)
            {
                _log("INFO", ProtocolConstants.MaaNopRunLogSource, rendered);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _warningLogged, 1) != 0)
            {
                return;
            }

            try
            {
                _log(
                    "WARN",
                    "maanop.callback",
                    $"MaaFramework Callback focus 解析失败：{exception.GetBaseException().Message}");
            }
            catch
            {
                // A logging failure must never escape the MaaFramework callback thread.
            }
        }
    }
}
