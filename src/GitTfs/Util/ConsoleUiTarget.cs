using System;
using NLog;
using NLog.Targets;

namespace GitTfs.Util
{
    [Target("ConsoleUi")]
    public sealed class ConsoleUiTarget : TargetWithLayout
    {
        private static readonly string[] ErrorPrefixes = { "error:", "error " };
        private static readonly string[] WarningPrefixes = { "warning:", "warning " };
        private static readonly string[] InfoPrefixes = { "info:", "info " };

        protected override void Write(LogEventInfo logEvent)
        {
            var message = Layout.Render(logEvent) ?? string.Empty;
            ConsoleColor color;
            if (StripPrefix(ref message, ErrorPrefixes) || logEvent.Level >= LogLevel.Error)
                color = ConsoleColor.Red;
            else if (StripPrefix(ref message, WarningPrefixes) || logEvent.Level == LogLevel.Warn)
                color = ConsoleColor.Yellow;
            else if (StripPrefix(ref message, InfoPrefixes))
                color = ConsoleColor.Cyan;
            else if (logEvent.Level <= LogLevel.Debug)
                color = ConsoleColor.DarkGray;
            else
                color = ConsoleColor.Gray;
            ConsoleUi.Log(message, color);
        }

        private static bool StripPrefix(ref string message, string[] prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    message = message.Substring(prefix.Length).TrimStart();
                    return true;
                }
            }
            return false;
        }
    }
}
