using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace CompanionApp.Startup;

internal sealed class CustomConsoleFormatter : ConsoleFormatter
{
    private readonly bool _logDateTime;

    public CustomConsoleFormatter(IOptions<ProxyConfig> options) : base("custom")
    {
        _logDateTime = options?.Value?.LogDateTime ?? false;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null)
        {
            return;
        }

        if (_logDateTime)
        {
            Span<char> buffer = stackalloc char[19];
            DateTime.Now.TryFormat(buffer, out _, "MM-dd HH:mm:ss.fff ");
            textWriter.Write(buffer);
        }

        textWriter.WriteLine(message);
    }
}