using Microsoft.ApplicationInsights;
using System;
using System.IO;
using System.Text;

/// <summary>
/// A TextWriter that writes to both an inner TextWriter and Application Insights.
/// </summary>
public class AppInsightsTextWriter : TextWriter
{
    private readonly TelemetryClient _telemetryClient;
    private readonly TextWriter _innerTextWriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppInsightsTextWriter"/> class.
    /// </summary>
    /// <param name="telemetryClient">The telemetry client for logging to Application Insights.</param>
    /// <param name="innerTextWriter">The inner text writer for logging to a text writer.</param>
    public AppInsightsTextWriter(TelemetryClient telemetryClient, TextWriter innerTextWriter)
    {
        _telemetryClient = telemetryClient;
        _innerTextWriter = innerTextWriter;
    }

    /// <summary>
    /// Writes a line of text to the text writer and Application Insights.
    /// </summary>
    /// <param name="value">The text to write.</param>
    public override void WriteLine(string? value)
    {
        if (value == null) return;
        base.WriteLine(value);
        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        if (value.StartsWith("\n\n")) {
            _innerTextWriter.WriteLine($"{timestamp} {value.Substring(2)}");
        } else {
            _telemetryClient.TrackTrace(value);
            _innerTextWriter.WriteLine($"{timestamp} {value}");
        }
    }

    /// <summary>
    /// Gets the encoding for the text writer.
    /// </summary>
    public override Encoding Encoding => Encoding.UTF8;
}