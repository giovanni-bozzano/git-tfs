using NLog;
using NLog.Targets;

namespace GitTfs.Util
{
    [Target("ConsoleUi")]
    public sealed class ConsoleUiTarget : TargetWithLayout
    {
        protected override void Write(LogEventInfo logEvent)
        {
            var message = Layout.Render(logEvent);
            var labelled = message.StartsWith("warning", System.StringComparison.OrdinalIgnoreCase)
                           || message.StartsWith("error", System.StringComparison.OrdinalIgnoreCase);
            if (labelled)
                ConsoleUi.Log(message);
            else if (logEvent.Level >= LogLevel.Error)
                ConsoleUi.Log("✗ " + message);
            else if (logEvent.Level == LogLevel.Warn)
                ConsoleUi.Log("! " + message);
            else
                ConsoleUi.Log(message);
        }
    }
}
