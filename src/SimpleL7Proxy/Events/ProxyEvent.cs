using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using System.Net;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.User;

namespace SimpleL7Proxy.Events
{

  public enum EventType
  {
    AsyncProcessing,
    Backend,
    BackendRequest,
    CircuitBreakerError,
    Console,
    CustomEvent,
    Exception,
    Poller,
    Probe,
    ProfileError,
    ProxyError,
    ProxyRequest,
    ProxyRequestEnqueued,
    ProxyRequestExpired,
    ProxyRequestRequeued,
    ServerError,
    Authentication,
  }

  public class ProxyEvent : ConcurrentDictionary<string, string>
  {
    private static IOptions<BackendOptions> _options = null!;
    private static IEventClient? _eventClient;
    private static TelemetryClient? _telemetryClient;
    private static readonly Uri LOCALHOSTURI = new Uri("http://localhost"); 

    public EventType Type { get; set; } = EventType.Console;
    public HttpStatusCode Status { get; set; } = 0;
    public Uri Uri { get; set; } = LOCALHOSTURI;
    public string? MID { get; set; } = "";
    public string? ParentId { get; set; } = "";
    public string? Method { get; set; } = "GET";
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public Exception? Exception { get; set; } = null;
    public static FrozenDictionary<string, string> DefaultParams { get; private set; } = FrozenDictionary<string, string>.Empty;

    public static void Initialize(
      IOptions<BackendOptions> backendOptions,
      IEventClient? eventClient = null,
      TelemetryClient? telemetryClient = null)
    {
      _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
      _eventClient = eventClient ?? throw new ArgumentNullException(nameof(eventClient));
      _telemetryClient = telemetryClient; // null when APPINSIGHTS_CONNECTIONSTRING is not set

      // Set default parameters that should be included with every event (frozen = immutable + optimized reads)
      DefaultParams = new Dictionary<string, string>(3)
      {
        ["Ver"] = Constants.VERSION,
        ["Revision"] = _options.Value.Revision,
        ["ContainerApp"] = _options.Value.ContainerApp
      }.ToFrozenDictionary();

    }

    /// <summary>
    /// Stamps Ver, Revision, ContainerApp, Status, Method into any properties dictionary.
    /// </summary>
    private void AddDefaultProperties(IDictionary<string, string> properties)
    {
      foreach (var kvp in DefaultParams)
      {
        properties[kvp.Key] = kvp.Value;
      }

      properties["Status"] = ((int)Status).ToString();
      properties["Method"] = Method ?? "GET";
    }

    public ProxyEvent() : base(1, 13, StringComparer.OrdinalIgnoreCase)
    {
    }

    public ProxyEvent(int capacity) : base(1, capacity, StringComparer.OrdinalIgnoreCase)
    {
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
        bool logToEventClient = false;

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
              logToEventClient = true;
            }
            break;
          case EventType.ServerError:
          case EventType.CircuitBreakerError:
            logEvent = true;
            logToEventClient = true;
            break;
          case EventType.Console:
            if (_options?.Value.LogConsole == true)
            {
              logEvent = true;
              logToEventClient = true;
            }
            break;
          case EventType.Poller:
            if (_options?.Value.LogPoller == true)
            {
              logEvent = true;
              logToEventClient = true;
            }
            break;
          case EventType.BackendRequest:
            logDependency = true;
            logToEventClient = true;
            break;
          case EventType.ProxyRequestEnqueued:
          case EventType.ProxyRequestRequeued:
            logEvent = true;
            logToEventClient = true;
            break;
          case EventType.ProxyRequestExpired:
          case EventType.ProxyError:
          case EventType.ProxyRequest:
            logRequest = true;
            logToEventClient = true;
            break;
          case EventType.Exception:
            logException = true;
            logToEventClient = true;
            break;
          default:
            // For any other event type, we can log it as a custom event
            logEvent = true;
            logToEventClient = true;
            break;
        }
        
        // Add replica-lifetime values at send time
        
        if (_telemetryClient is not null)
        {
          if (logDependency) TrackDependancy();
          else if (logRequest) TrackRequest();
          else if (logEvent) TrackEvent();
          else if (logException) TrackException();
        }

        if (logToEventClient && _eventClient is not null)
        {
          Dictionary<string, string> eventParams = new Dictionary<string, string>(DefaultParams, StringComparer.OrdinalIgnoreCase);
          eventParams["Type"] = "S7P-" + Type.ToString();
          eventParams["MID"] = MID ?? "N/A";
          AddDefaultProperties(eventParams);
          // Send the event to all registered event clients (EventHub, LogFile, etc.)
          _eventClient.SendData(ConvertToJson(this, eventParams));
        }
      }
      catch (Exception ex)
      {
        // Prevent telemetry errors from affecting application operation
        Console.Error.WriteLine($"Error sending telemetry: {ex.Message}");
      }
    }

    public static string ConvertToJson(ProxyEvent proxyEvent, IDictionary<string, string>? extraProperties = null)
    {
        // Use Utf8JsonWriter to merge proxyEvent + extraProperties into one JSON object
        // without allocating an intermediate merged dictionary
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var kvp in proxyEvent)
            {
                writer.WriteString(kvp.Key, kvp.Value);
            }

            if (extraProperties is not null)
            {
                foreach (var kvp in extraProperties)
                {
                    writer.WriteString(kvp.Key, kvp.Value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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

      // Stamp defaults directly into telemetry (not into this ProxyEvent)
      AddDefaultProperties(eventTelemetry.Properties);

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
      AddDefaultProperties(dependencyTelemetry.Properties);

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
      requestTelemetry.Properties["HttpMethod"] = Method ?? "GET";
      requestTelemetry.Source = "S7P"; // Custom source identifier
      requestTelemetry.Duration = Duration;
      requestTelemetry.Context.Operation.Id = requestId;
      requestTelemetry.Context.Operation.ParentId = ParentId;

      // Add a special flag to mark this as our custom telemetry
      requestTelemetry.Properties["CustomTracked"] = "true";
      AddDefaultProperties(requestTelemetry.Properties);

      foreach (var kvp in this)
      {
        requestTelemetry.Properties.Add(kvp);
      }

      _telemetryClient?.TrackRequest(requestTelemetry);
    }

    private void TrackException()
    {
      this["ExceptionType"] = Exception?.GetType().ToString() ?? "Unknown";
      this["Message"] = Exception?.Message ?? "No exception message";
      AddDefaultProperties(this);

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
          _eventClient?.SendData(ConvertToJson(this));
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

        _eventClient?.SendData(ConvertToJson(this));
      }
      catch (Exception ex)
      {
        // Handle any exceptions that occur during logging
        Console.Error.WriteLine($"Error writing output: {ex.Message}");
      }
    }

    public Dictionary<string, string> ToDictionary(List<string>? keys = null)
    {
      // Create a new dictionary to hold the properties
      var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      dict["Status"] = ((int)Status).ToString();
      dict["Reason"] = Status.ToString();
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

    /// <summary>
    /// Clears all dictionary entries and resets properties to release memory.
    /// Call this after SendEvent() to prevent memory leaks.
    /// </summary>
    public void ClearEventData()
    {
      base.Clear();
      Exception = null;
    }
  }

}