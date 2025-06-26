using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using System.Net;

public enum EventType
{
  Backend,
  BackendRequest,
  CircuitBreakerError,
  Console,
  CustomEvent,
  Exception,
  Poller,
  Probe,
  ProxyError,
  ProxyRequest,
  ProxyRequestEnqueued,
  ProxyRequestExpired,
  ProxyRequestRequeued,
  ServerError,
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
  public string? ParentId { get; set; } = "";
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
    ParentId = other.ParentId;
    Method = other.Method;
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
      bool logDependency = false;
      bool logRequest = false;
      bool logException = false;
      bool logToEventHub = false;

      // Console.WriteLine($"Sending event: {Type} with Status: {Status} and Duration: {Duration.TotalMilliseconds} ms");

      // Determine the type of telemetry to send based on event type
      switch (Type)
      {
        case EventType.Backend:
        case EventType.CustomEvent:
        case EventType.Probe:
          if (_options?.Value.LogProbes == true)
          {
            logEvent = true;
            logToEventHub = true;
          }
          break;
        case EventType.ServerError:
        case EventType.CircuitBreakerError:
          logEvent = true;
          logToEventHub = true;
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
        case EventType.BackendRequest:
          logDependency = true;
          logToEventHub = true;
          break;
        case EventType.ProxyRequestEnqueued:
        case EventType.ProxyRequestRequeued:
          logEvent = true;
          logToEventHub = true;
          break;
        case EventType.ProxyRequestExpired:
        case EventType.ProxyError:
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
        if (logDependency) TrackDependancy();
        else if (logRequest) TrackRequest();
        else if (logEvent) TrackEvent();
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

    var eventTelemetry = new EventTelemetry(eventName);
    eventTelemetry.Metrics["Duration"] = Duration.TotalMilliseconds;
    // eventTelemetry.Name = eventName;

    // Set operation context if available
    if (!string.IsNullOrEmpty(MID))
    {
      eventTelemetry.Context.Operation.Id = MID;
      if (!string.IsNullOrEmpty(ParentId))
      {
        eventTelemetry.Context.Operation.ParentId = ParentId;
      }
    }

    // Add all properties except MID and ParentId (which go in the operation context)
    foreach (var kvp in this)
    {
      // Skip MID and ParentId as they belong in the operation context
      if (kvp.Key != "MID" && kvp.Key != "ParentId" && kvp.Key != "OperationId")
      {
        eventTelemetry.Properties[kvp.Key] = kvp.Value;
      }
    }

    _telemetryClient?.TrackEvent(eventTelemetry);
  }

private void TrackDependancy()
  {
    // Create a dependency telemetry instance
    var dependencyTelemetry = new DependencyTelemetry
    {
      Name = Method + " " + Uri.Segments[^1],
      Data = Uri.ToString(),
      Type = "HTTP", // Can be HTTP, SQL, Azure blob, etc.
      Target = Uri.Host,
      Duration = Duration,
      Success = (int)Status >= 200 && (int)Status < 400,
      ResultCode = ((int)Status).ToString()
    };

    // Set the timestamp
    dependencyTelemetry.Timestamp = DateTimeOffset.UtcNow.Subtract(Duration);
    dependencyTelemetry.Id = MID;
    // Add custom properties
    foreach (var kvp in this)
    {
      dependencyTelemetry.Properties[kvp.Key] = kvp.Value;
    }

    // Add context if available
    if (!string.IsNullOrEmpty(MID))
    {
      dependencyTelemetry.Context.Operation.Id = MID;
      dependencyTelemetry.Context.Operation.ParentId = ParentId;
    }

    _telemetryClient?.TrackDependency(dependencyTelemetry);
  }
  private void TrackRequest()
  {
    // Check if we've already tracked this request using the MID as a key
    var requestId = MID ?? Guid.NewGuid().ToString();

    var success = (int)Status >= 200 && (int)Status < 400;
    var requestTelemetry = new RequestTelemetry
    {
      Name = Method + " " + Uri.Segments[^1],
      Url = Uri,
      ResponseCode = Status.ToString(),
      Success = success,
      Id = requestId // Set a consistent ID to help identify duplicates
    };

    requestTelemetry.Timestamp = DateTimeOffset.UtcNow.Subtract(Duration);
    requestTelemetry.HttpMethod = Method ?? "GET"; // Default to GET if Method is null
    requestTelemetry.Source = "S7P"; // Custom source identifier
    requestTelemetry.Duration = Duration;
    requestTelemetry.Context.Operation.Id = requestId;
    requestTelemetry.Context.Operation.ParentId = ParentId;

    // Add a special flag to mark this as our custom telemetry
    requestTelemetry.Properties["CustomTracked"] = "true";

    foreach (var kvp in this)
    {
      requestTelemetry.Properties.Add(kvp);
    }

    _telemetryClient?.TrackRequest(requestTelemetry);
  }

  private void TrackException()
  {
    _telemetryClient?.TrackException(Exception, this.ToDictionary());
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
  
  public Dictionary<string, string> ToDictionary(string[]? keys = null)
  {
    // Create a new dictionary to hold the properties
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    dict["Status"] = ((int)Status).ToString();
    dict["Duration"] = Duration.TotalMilliseconds.ToString();

    if (keys != null)
    {
      foreach (var key in keys)
      {
        if (TryGetValue(key, out var value))
        {
          dict[key] = value;
        }
      }

      return dict;
    }

    // Convert the ProxyEvent to a dictionary for telemetry
    return this.ToDictionary((kvp) => kvp.Key, (kvp) => kvp.Value, StringComparer.OrdinalIgnoreCase);
  }
}

