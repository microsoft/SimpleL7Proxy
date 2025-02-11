using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System;
using System.IO;

public class CustomConsoleFormatter : ConsoleFormatter
{
    public CustomConsoleFormatter() : base("custom") { }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var logLevel = logEntry.LogLevel;
        var eventId = logEntry.EventId;
        var state = logEntry.State;
        var exception = logEntry.Exception;
        var message = logEntry.Formatter(state, exception);

        textWriter.WriteLine($"{DateTime.Now:yyyy-MM-ddTHH:mm:ss} {message}");
    }
}