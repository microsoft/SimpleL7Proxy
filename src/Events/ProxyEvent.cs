using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using System.Net;

public enum EventType
{
  ProxyRequestEnqueued,
  ProxyRequest,
  ProxyRequestExpired,
  ProxyRequestRequeued,
  ProxyError,
  Exception,
  Backend,
  ServerError,
  Probe,
  CustomEvent,
  Console,
  CircuitBreakerError,
  Poller,
}

public class ProxyEvent : ConcurrentDictionary<string, string>
{
  private static IOptions<BackendOptions> _options = null!;
  private static IEventHubClient? _eventHubClient;
  private static TelemetryClient? _telemetryClient;

  public EventType Type { get; set; } = EventType.Console;
  public HttpStatusCode Status { get; set; } = 0;
  public Uri Uri { get; set; } = new Uri("http://localhost");
  public string? MID { get; set; } = "";
  public string? Method { get; set; } = "GET"; 
  public TimeSpan Duration { get; set; } = TimeSpan.Zero;
  public Exception? Exception { get; set; } = null;

  public static void Initialize(
    IOptions<BackendOptions> backendOptions,
    IEventHubClient? eventHubClient = null,
    TelemetryClient? telemetryClient = null)
  {
    _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
    _eventHubClient = eventHubClient ?? throw new ArgumentNullException(nameof(eventHubClient));
    _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));

  }
  public ProxyEvent() : base()
  {
    base["Ver"] = Constants.VERSION;
    base["Revision"] = _options.Value.Revision;
    base["ContainerApp"] = _options.Value.ContainerApp;
  }

  public ProxyEvent(ProxyEvent other) : base(other)
  {
    if (other == null) throw new ArgumentNullException(nameof(other));
    Type = other.Type;
    Status = other.Status;
    Uri = other.Uri;
    MID = other.MID;
    Duration = other.Duration;
    Exception = other.Exception;
  }

  /// <summary>
  /// Sends the event to the configured telemetry destinations
  /// </summary>
  public void SendEvent()
  {
    try
    {
      bool logEvent = false;
      bool logRequest = false;
      bool logException = false;
      bool logToEventHub = false;

      // Determine the type of telemetry to send based on event type
      switch (Type)
      {
        case EventType.ProxyRequestEnqueued:
        case EventType.ProxyRequestExpired:
        case EventType.ProxyRequestRequeued:
        case EventType.ProxyError:
        case EventType.Backend:
        case EventType.ServerError:
        case EventType.CustomEvent:
        case EventType.CircuitBreakerError:
        case EventType.Probe:
        if (_options?.Value.LogProbes == true)
          {
            logEvent = true;
            logToEventHub = true;
          }
          break;
        case EventType.Console:
          if (_options?.Value.LogConsole == true)
          {
            logEvent = true;
            logToEventHub = true;
          }
          break;
        case EventType.Poller:
          if (_options?.Value.LogPoller == true)
          {
            logEvent = true;
            logToEventHub = true;
          }
          break;
        case EventType.ProxyRequest:
          logRequest = true;
          logToEventHub = true;
          break;
        case EventType.Exception:
          logException = true;
          logToEventHub = true;
          break;
        default:
          // For any other event type, we can log it as a custom event
          logEvent = true;
          logToEventHub = true;
          break;
      }

      this["Status"] = ((int)Status).ToString();
      this["Method"] = Method ?? "GET"; // Default to GET if Method is null
      if (_telemetryClient is not null)
      {
        if (logEvent) TrackEvent();
        else if (logRequest) TrackRequest();
        else if (logException) TrackException();
      }

      if (logToEventHub && _eventHubClient is not null)
      {
        this["Type"] = "S7P-" + Type.ToString();
        this["MID"] = MID ?? "N/A"; 
        // Send the event to Event Hub
        _eventHubClient.SendData(this);
      }
    }
    catch (Exception ex)
    {
      // Prevent telemetry errors from affecting application operation
      Console.Error.WriteLine($"Error sending telemetry: {ex.Message}");
    }
  }

  private void TrackEvent()
  {
    string eventName = "S7P-" + Type.ToString();
    Dictionary<string, double> metrics = new()
    {
      ["Duration"] = Duration.TotalMilliseconds
    };

    _telemetryClient?.TrackEvent(eventName, this.ToDictionary(), metrics);
  }

  private void TrackRequest()
  {

    var success = (int)Status >= 200 && (int)Status < 400;
    var requestTelemetry = new RequestTelemetry
    {
      Name = Method + " " + Uri.Segments[^1],
      Url = Uri,
      ResponseCode = Status.ToString(),
      Success = success
    };

    requestTelemetry.Duration = Duration;
    requestTelemetry.Context.Operation.Id = MID ?? Guid.NewGuid().ToString();

    foreach (var kvp in this)
    {
      requestTelemetry.Properties.Add(kvp);
    }

    _telemetryClient?.TrackRequest(requestTelemetry);
  }

  private void TrackException()
  {
    _telemetryClient!.TrackException(Exception, this.ToDictionary());
  }

  // private void TrackAvailability()
  // {
  //     if (!TryGetValue("Probe", out string? probeName) ||
  //         !TryGetValue("ProbeStatus", out string? statusCode))
  //     {
  //         // Fall back to tracking as a custom event if required fields are missing
  //         TrackEvent();
  //         return;
  //     }

  //     var properties = GetTelemetryProperties();
  //     var success = statusCode == "200";
  //     var availabilityTelemetry = new AvailabilityTelemetry
  //     {
  //         Name = $"Probe-{probeName}",
  //         Success = success,
  //         RunLocation = TryGetValue("Region", out string? region) ? region : "Unknown",
  //         Message = TryGetValue("ProbeMessage", out string? message) ? message : null
  //     };

  //     if (TryGetValue("x-Total-Latency", out string? duration) && 
  //         double.TryParse(duration, out double durationMs))
  //     {
  //         availabilityTelemetry.Duration = TimeSpan.FromMilliseconds(durationMs);
  //     }

  //     foreach (var prop in properties)
  //     {
  //         availabilityTelemetry.Properties.Add(prop.Key, prop.Value);
  //     }

  //     _telemetryClient!.TrackAvailability(availabilityTelemetry);
  // }
  public void WriteOutput(string data = "")
  {

    try
    {
      // Log the data to the console
      if (!string.IsNullOrEmpty(data))
      {
        Console.WriteLine(data);
        this["Message"] = data;
      }

      Type = EventType.Console;

      if (_options.Value.LogConsoleEvent)
      {
        _eventHubClient?.SendData(this);
      }
    }
    catch (Exception ex)
    {
      // Handle any exceptions that occur during logging
      Console.WriteLine($"Error writing output: {ex.Message}");
    }
  }

  public void WriteErrorOutput(string data = "")
  {

    try
    {
      // Log the data to the console
      if (!string.IsNullOrEmpty(data))
      {
        Console.Error.WriteLine(data);
        this["Message"] = data;
      }

      if (!TryGetValue("Type", out var typeValue))
      {
        this["Type"] = "S7P-Console-Error";
      }

      _eventHubClient?.SendData(this);
    }
    catch (Exception ex)
    {
      // Handle any exceptions that occur during logging
      Console.Error.WriteLine($"Error writing output: {ex.Message}");
    }
  }
}

