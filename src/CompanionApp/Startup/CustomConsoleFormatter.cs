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
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        if (_logDateTime)
        {
            Span<char> buffer = stackalloc char[19];
            DateTime.Now.TryFormat(buffer, out _, "MM-dd HH:mm:ss.fff ");
            textWriter.Write(buffer);
        }

        if (logEntry.LogLevel is LogLevel.Warning or LogLevel.Error or LogLevel.Critical)
        {
            textWriter.Write(logEntry.LogLevel.ToString().ToUpperInvariant());
            textWriter.Write(": ");
        }

        if (logEntry.LogLevel is LogLevel.Debug or LogLevel.Trace)
        {
            textWriter.Write(logEntry.Category);
            textWriter.Write(" | ");
            var replica = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME")
                ?? Environment.GetEnvironmentVariable("HOSTNAME");
            var revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION");
            if (!string.IsNullOrWhiteSpace(replica))
            {
                textWriter.Write("Instance=");
                textWriter.Write(replica);
                textWriter.Write(" | ");
            }
            if (!string.IsNullOrWhiteSpace(revision))
            {
                textWriter.Write("Revision=");
                textWriter.Write(revision);
                textWriter.Write(" | ");
            }
        }
        textWriter.WriteLine(message);
        if (logEntry.Exception is not null)
        {
            if (logEntry.LogLevel is LogLevel.Debug or LogLevel.Trace)
            {
                textWriter.WriteLine(logEntry.Exception);
            }
            else
            {
                textWriter.WriteLine(logEntry.Exception.Message);
                if (logEntry.Exception.InnerException is not null)
                {
                    textWriter.WriteLine(logEntry.Exception.GetBaseException().Message);
                }
            }
        }
    }
}