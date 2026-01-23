using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace SimpleL7Proxy.Logging;

public sealed class SimpleTimestampConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "simpleTimestamp";

    public SimpleTimestampConsoleFormatter() : base(FormatterName)
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        textWriter.Write(timestamp);
        textWriter.Write(' ');
        textWriter.Write(message);

        if (logEntry.Exception is not null)
        {
            textWriter.Write(" | ");
            textWriter.Write(logEntry.Exception);
        }

        textWriter.WriteLine();
    }
}
