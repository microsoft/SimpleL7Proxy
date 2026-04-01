using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System;
using System.IO;

using SimpleL7Proxy.Config;

public class CustomConsoleFormatter : ConsoleFormatter
{
    private readonly bool _logDateTime;

    public CustomConsoleFormatter(IOptions<ProxyConfig> options) : base("custom")
    {
        _logDateTime = options?.Value?.LogDateTime ?? false;
        Console.WriteLine($"[CONFIG] CustomConsoleFormatter initialized with LogDateTime={_logDateTime}");
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null) return;

        if (_logDateTime)
        {
            Span<char> buf = stackalloc char[19]; // "MM-dd HH:mm:ss.fff"
            DateTime.Now.TryFormat(buf, out _, "MM-dd HH:mm:ss.fff ");
            textWriter.Write(buf);
            textWriter.WriteLine(message);
        }
        else
        {
            textWriter.WriteLine(message);
        }
    }
}