using Microsoft.ApplicationInsights;
using System;
using System.IO;
using System.Text;

public class AppInsightsTextWriter : TextWriter
{
    private readonly TelemetryClient _telemetryClient;
    private readonly TextWriter _innerTextWriter;

    public AppInsightsTextWriter(TelemetryClient telemetryClient, TextWriter innerTextWriter)
    {
        _telemetryClient = telemetryClient;
        _innerTextWriter = innerTextWriter;
    }

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

    public override Encoding Encoding => Encoding.UTF8;
}