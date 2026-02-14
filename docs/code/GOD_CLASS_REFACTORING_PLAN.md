# God Classes & Methods: Concrete Refactoring Plan

## Overview

This document provides a **concrete, line-by-line plan** for breaking down the four God classes in the codebase. Each proposal identifies specific methods and line ranges to extract, explains why the extraction makes sense, and describes the target class structure.

The four God classes are:
| Class | File | LOC | Constructor Deps | Key Issue |
|-------|------|-----|-----------------|-----------|
| `ProxyWorker` | `Proxy/ProxyWorker.cs` | 1,756 | 16 params | Two 500+ LOC methods that combine 6+ responsibilities |
| `Server` | `server.cs` | 641 | 12 params | Single 455-line `Run()` method doing everything |
| `UserProfile` | `User/UserProfile.cs` | 869 | 2 params | 9 distinct responsibilities in one class |
| `RequestData` | `RequestData.cs` | 585 | Static services | Data class with hidden service-call side effects in setters |

**What's already been extracted (good work done):**
- `RequestLifecycleManager` (346 LOC) — status transitions
- `EventDataBuilder` (159 LOC) — telemetry data population
- `ProxyHelperUtils` (206 LOC) — header copy, error generation
- `AsyncWorker` (798 LOC) — async blob/SB coordination
- `StreamProcessorFactory` + processor hierarchy — stream processing

---

## 1. ProxyWorker.cs (1,756 LOC) — Split into 4 Focused Classes

### Why it's a problem
`ProxyWorker` has **16 constructor parameters**, **two God methods** (`TaskRunnerAsync` at ~430 LOC and `ProxyToBackEndAsync` at ~560 LOC), and mixes 6 responsibilities: queue management, backend routing, response writing, async coordination, error handling, and telemetry. No single developer can hold the entire `ProxyToBackEndAsync` in their head. It has deeply nested try/catch/finally blocks with interleaved async logic.

### 1A. Extract `BackendRequestExecutor` (from `ProxyToBackEndAsync`)

**What to move** (lines 791–1449, ~660 LOC):
- The entire `ProxyToBackEndAsync()` method → becomes the main method of a new class
- `SetupAsyncWorkerAndTimeout()` (lines 1687–1717)
- `CaptureResponseStream()` (lines 1452–1487)
- `PopulateRequestAttemptError()` (lines 1490–1499)
- `PopulateTimeoutError()` (lines 1502–1515)
- `IsInvalidHeaderException()` (line 1517–1518)
- `TryGetNextHost()` (lines 1736–1755) 
- `s_excludedHeaders` static field (lines 1720–1725)
- `s_backendRequestAttemptEvent` static field (line 62)

**New class signature:**
```csharp
public class BackendRequestExecutor
{
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly IAsyncWorkerFactory _asyncWorkerFactory;
    private readonly ILogger<BackendRequestExecutor> _logger;
    private readonly RequestLifecycleManager _lifecycleManager;
    private readonly StreamProcessorFactory _streamProcessorFactory;
    private readonly ISharedIteratorRegistry? _sharedIteratorRegistry;
    
    public async Task<ProxyData> ExecuteAsync(RequestData request) { ... }
}
```

**Why this makes sense:**
- `ProxyToBackEndAsync` is a self-contained algorithm: "given a request, iterate backends, send request, capture response." It reads from `RequestData` and returns `ProxyData` — a clear input→output contract.
- It has its own distinct error handling patterns (S7PRequeueException, 429 retry logic, circuit breaker tracking) that are orthogonal to the worker loop's error handling.
- The 5 helper methods it calls (`SetupAsyncWorkerAndTimeout`, `CaptureResponseStream`, `PopulateRequestAttemptError`, `PopulateTimeoutError`, `IsInvalidHeaderException`) are all private and used exclusively by `ProxyToBackEndAsync` — they should move with it.
- This extraction reduces `ProxyWorker` from 16 to ~10 constructor dependencies.

---

### 1B. Extract `ResponseWriter` (from `WriteResponseAsync` + `StreamResponseAsync`)

**What to move** (lines 602–1683, ~1080 LOC but much overlaps with 1A):
- `WriteResponseAsync()` (lines 602–691, ~90 LOC)
- `StreamResponseAsync()` (lines 1541–1650, ~110 LOC)
- `HandleBackgroundCheckResultAsync()` (lines 1652–1683, ~32 LOC)

**New class signature:**
```csharp
public class ResponseWriter
{
    private readonly StreamProcessorFactory _streamProcessorFactory;
    private readonly RequestLifecycleManager _lifecycleManager;
    private readonly ILogger<ResponseWriter> _logger;
    
    public async Task WriteAsync(RequestData request, ProxyData proxyData) { ... }
    private async Task StreamAsync(RequestData request, ProxyData proxyData) { ... }
    private async Task HandleBackgroundCheckAsync(...) { ... }
}
```

**Why this makes sense:**
- Response writing is a distinct concern from backend routing. `WriteResponseAsync` sets HTTP status codes and content headers on the `HttpListenerResponse`, then delegates to `StreamResponseAsync` which picks a stream processor and pipes data to the output stream.
- The method has three distinct output targets (client HTTP, async blob, memory buffer for background checks) — this complexity deserves its own class.
- It has its own error handling pattern (swallowing flush errors at line 687-690 with `LogDebug`) which is a bug that's easier to spot in a focused class.

---

### 1C. Extract `ErrorResponseWriter` (from exception catch blocks in `TaskRunnerAsync`)

**What to move** (scattered across lines 382–590):
- The `ProxyErrorException` catch block (lines 389–421) — writes error message to `lcontext.Response.OutputStream`
- The `IOException` catch block (lines 423–458) — writes "IO Exception" to response
- The generic `Exception` catch block (lines 470–520) — writes "Exception" to response
- The finally block error event logic (lines 522–590) — copies event data and sends error event

**New class signature:**
```csharp
public class ErrorResponseWriter
{
    private readonly ILogger<ErrorResponseWriter> _logger;
    private readonly RequestLifecycleManager _lifecycleManager;
    
    public async Task WriteProxyErrorAsync(RequestData request, HttpListenerContext context, ProxyErrorException ex) { ... }
    public async Task WriteIOErrorAsync(RequestData request, HttpListenerContext context, IOException ex) { ... }
    public async Task WriteGenericErrorAsync(RequestData request, HttpListenerContext context, Exception ex) { ... }
}
```

**Why this makes sense:**
- All three catch blocks follow an identical pattern: (1) set lifecycle status, (2) set event data, (3) try to write error to context.Response.OutputStream, (4) catch write failures. This is ~130 LOC of duplicated structure.
- The error writing has its own try-catch-within-catch pattern that's hard to read in the context of the main worker loop.
- Having a dedicated `ErrorResponseWriter` makes it testable — you can verify that a `ProxyErrorException` produces the right status code and body without running the entire worker loop.

---

### 1D. What remains in `ProxyWorker` after extractions

After extracting 1A, 1B, 1C, `ProxyWorker.TaskRunnerAsync()` shrinks from ~430 LOC to ~120 LOC:

```csharp
public async Task TaskRunnerAsync()
{
    // Startup: IncrementActiveWorkers (lines 179-185)
    // Main loop: dequeue → validate → call BackendRequestExecutor → call ResponseWriter
    // Exception handling: delegate to ErrorResponseWriter
    // Finally: cleanup + dispose
    // Shutdown: DecrementActiveWorkers
}
```

Constructor drops from 16 params to ~8 (queue, options, lifecycle manager, executor, writer, error writer, health service, logger).

---

## 2. server.cs `Run()` (455 LOC) — Extract 4 Inline Responsibilities

### Why it's a problem
`Server.Run()` (lines 186–641) is a single method that:
1. Accepts HTTP connections (lines 198–220)
2. Fast-paths health probes (lines 222–283)
3. Validates request headers (lines 286–396) — auth, disallowed headers, user profiles, required headers, validate headers
4. Configures async mode (lines 418–438)
5. Calculates priority (lines 440–476)
6. Checks circuit breaker + enqueues (lines 478–521)
7. Handles enqueue failures with error responses (lines 564–603)
8. Sends telemetry events (lines 607–614)

All of this is in a single deeply-nested `while` loop with 7+ indentation levels.

### 2A. Extract `RequestValidator` (from inline header validation)

**What to move** (lines 286–395, ~110 LOC):
- AuthAppID validation (lines 292–311)
- Disallowed header removal (lines 313–319)
- User profile lookup and header enrichment (lines 322–354)
- Required header checks (lines 357–372)
- Validate header cross-checks (lines 375–395)

**New class signature:**
```csharp
public class RequestValidator
{
    private readonly BackendOptions _options;
    private readonly IUserProfileService _userProfile;
    private readonly ILogger<RequestValidator> _logger;
    
    /// <summary>
    /// Validates request headers and enriches with user profile data.
    /// Throws ProxyErrorException for invalid auth, missing headers, or unknown profiles.
    /// </summary>
    public void ValidateAndEnrich(RequestData request) { ... }
}
```

**Why this makes sense:**
- This is a linear pipeline of validation checks — each one independent and testable.
- Currently, a bug in header validation requires reading the entire 455-line `Run()` to find. In a dedicated class, each check is a single method.
- The validation logic is pure (reads headers, throws exceptions) — no side effects beyond `rd.Headers.Set()`.

---

### 2B. Extract `RequestPriorityCalculator` (from inline priority logic)

**What to move** (lines 397–476, ~80 LOC):
- UserID computation from UniqueUserHeaders (lines 398–409)
- Priority boost via `_userPriority.addRequest/boostIndicator` (lines 441–443)
- Priority key lookup from `_options.PriorityKeys` (lines 447–455)
- Timeout header parsing (lines 463–471)
- TTL/expiration calculation (line 474)

**New class signature:**
```csharp
public class RequestPriorityCalculator
{
    private readonly BackendOptions _options;
    private readonly IUserPriorityService _userPriority;
    
    /// <summary>
    /// Computes priority, user ID, priority boost, timeout, and expiration for a request.
    /// </summary>
    public (int priority, int boost, Guid guid) Calculate(RequestData request) { ... }
}
```

**Why this makes sense:**
- Priority calculation is a pure computation with no I/O — it reads headers and config, produces numbers.
- It's currently interleaved with telemetry logging (`ed["UserID"] = ...`) making it hard to reason about what's computation vs. observation.
- Testability: you can unit test that priority key "gold" maps to priority value 1 without an HTTP listener.

---

### 2C. Extract `EnqueueDecisionMaker` (from circuit breaker + queue checks)

**What to move** (lines 477–521, ~45 LOC):
- Circuit breaker check (lines 478–486)
- Queue full check (lines 487–494)
- No active hosts check (lines 495–502)
- Enqueue attempt (lines 507–514)

**New class signature:**
```csharp
public class EnqueueDecisionMaker
{
    private readonly IBackendService _backends;
    private readonly IConcurrentPriQueue<RequestData> _requestsQueue;
    private readonly BackendOptions _options;
    
    /// <summary>
    /// Determines whether a request can be enqueued and performs the enqueue.
    /// Returns (success, httpStatusCode, message) tuple.
    /// </summary>
    public (bool enqueued, int failCode, string failMessage) TryEnqueue(
        RequestData request, int priority, int boost) { ... }
}
```

**Why this makes sense:**
- The circuit breaker → queue full → no hosts → enqueue chain is a decision tree. Currently it uses 4 if/else-if blocks with `notEnqued` / `notEnquedCode` / `retrymsg` / `logmsg` sentinel variables — a classic code smell.
- The same 429 response pattern (`notEnqued = true; notEnquedCode = 429; retrymsg = ...`) is repeated 4 times in 40 lines. A single method returning a result tuple eliminates all sentinel variables.

---

### 2D. Extract `AsyncRequestConfigurator` (from inline async setup)

**What to move** (lines 417–438, ~22 LOC):
- Async client request header check
- `_userProfile.GetAsyncParams()` call
- Setting `rd.runAsync`, `rd.BlobContainerName`, `rd.SBTopicName`, etc.

**New class signature:**
```csharp
public class AsyncRequestConfigurator
{
    private readonly BackendOptions _options;
    private readonly IUserProfileService _userProfile;
    
    public void ConfigureIfAsync(RequestData request) { ... }
}
```

**Why this makes sense:**
- Async configuration is only relevant when `doAsync && header == "true"`. Extracting it means sync-only deployments can skip this code path entirely.
- The async config parsing depends on `GetAsyncParams()` which itself is a complex method in UserProfile — having a dedicated configurator makes the dependency explicit.

---

### 2E. What remains in `Server.Run()` after extractions

```csharp
public async Task Run(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var context = await AcceptConnection();  // ~20 LOC
        if (IsProbe(context)) { HandleProbe(context); continue; }  // ~15 LOC
        
        var request = new RequestData(context, requestId);
        _validator.ValidateAndEnrich(request);           // was 110 LOC inline
        _asyncConfigurator.ConfigureIfAsync(request);    // was 22 LOC inline
        var (pri, boost, guid) = _priorityCalc.Calculate(request);  // was 80 LOC inline
        var (ok, code, msg) = _enqueueDecision.TryEnqueue(request, pri, boost);  // was 45 LOC inline
        
        if (!ok) { WriteErrorResponse(context, code, msg); }
        else { SendEnqueuedEvent(request); }
    }
}
```

This reduces `Run()` from **455 LOC** to **~60 LOC** — easy to read, easy to test, easy to modify.

---

## 3. UserProfile.cs (869 LOC) — Split into 4 Focused Classes

### Why it's a problem
`UserProfile` is a `BackgroundService` that handles 9 distinct responsibilities: HTTP config fetching, JSON parsing, user profile storage, soft-delete lifecycle, async client config validation, Azure name validation, health/staleness tracking, event logging, and suspended user tracking. The constructor only takes 2 params but the class has 14 private fields including volatile dictionaries, locks, and a static HttpClient.

### 3A. Extract `UserConfigLoader` (from `ConfigReader` + `ReadUserConfigAsync`)

**What to move:**
- `ConfigReader()` (lines 110–266, ~157 LOC) — the background polling loop
- `ReadUserConfigAsync()` (lines 273–325, ~53 LOC) — file/URL reading
- The `ExecuteAsync()` Task.Run loop (lines 68–108) — background service plumbing
- `lastSuccessfulProfileLoad`, `profilesAreStale`, `staleDuration`, `isInitialized` fields
- The staleness tracking and health status reporting (lines 219–263)

**New class signature:**
```csharp
public class UserConfigLoader : BackgroundService
{
    private readonly BackendOptions _options;
    private readonly IUserConfigParser _parser;
    private readonly ILogger<UserConfigLoader> _logger;
    
    // Owns the polling loop, HTTP fetching, and staleness tracking
    protected override Task ExecuteAsync(CancellationToken stoppingToken) { ... }
    public bool ServiceIsReady() { ... }
}
```

**Why this makes sense:**
- The config reading loop (`ConfigReader`) is infrastructure code — it handles HTTP retries, staleness detection, and error delays. This is completely independent of what the config *contains*.
- Today, a change to how staleness is tracked requires touching the same file as JSON parsing logic — violating SRP.
- The polling loop has its own timing logic (`NormalDelayMs`, `ErrorDelayMs`, `remainingDelay`) that's orthogonal to profile storage.

---

### 3B. Extract `UserConfigParser` (from `ParseUserConfig`)

**What to move:**
- `ParseUserConfig()` (lines 343–471, ~129 LOC) — JSON deserialization and mode-based parsing
- `ParsingMode` enum (lines 54–59)

**New class signature:**
```csharp
public interface IUserConfigParser
{
    void Parse(string fileContent, ParsingMode mode);
}

public class UserConfigParser : IUserConfigParser
{
    private readonly IUserProfileRepository _repository;
    private readonly BackendOptions _options;
    private readonly ILogger<UserConfigParser> _logger;
    
    public void Parse(string fileContent, ParsingMode mode) { ... }
}
```

**Why this makes sense:**
- `ParseUserConfig` contains ~130 LOC of JSON deserialization logic (iterating `JsonElement.EnumerateArray()`, mode-based branching, reserved key filtering). This is parsing logic that should be separate from storage.
- The method currently writes directly to three volatile fields (`userProfiles`, `suspendedUserProfiles`, `authAppIDs`) via `Interlocked.Exchange`. Extracting it means the parser produces data that the repository stores — clean separation.
- The `ParsingMode` enum and the per-mode branching (`if (mode == ParsingMode.SuspendedUserMode)` etc.) is a strategy pattern waiting to happen.

---

### 3C. Extract `SoftDeleteManager` (from `ApplySoftDeletes` + `CheckSoftDeleteStatus`)

**What to move:**
- `ApplySoftDeletes()` (lines 770–867, ~98 LOC) — marks missing entries, handles grace periods
- `CheckSoftDeleteStatus()` (lines 559–596, ~38 LOC) — checks if entry is valid/expired
- `CleanSoftDeleteMarkers()` (lines 717–723, ~7 LOC)
- `SoftDeleteExpirationPeriod` field
- `DeletedAtKey`, `ExpiresAtKey` constants (lines 35–36)

**New class signature:**
```csharp
public class SoftDeleteManager
{
    private readonly TimeSpan _expirationPeriod;
    private readonly ILogger<SoftDeleteManager> _logger;
    private readonly ProxyEvent _errorEvent;  // or IEventClient
    
    public const string DeletedAtKey = "__DeletedAt";
    public const string ExpiresAtKey = "__ExpiresAt";
    
    public (Dictionary<string, Dictionary<string, string>> result, int deletedCount, ...) 
        ApplySoftDeletes(currentData, newData, entityType) { ... }
    
    public (bool isValid, bool isSoftDeleted) 
        CheckStatus(Dictionary<string, string> entry, string entityId, string entityType) { ... }
    
    public Dictionary<string, string> CleanMarkers(Dictionary<string, string> entry) { ... }
}
```

**Why this makes sense:**
- Soft-delete is a **cross-cutting concern** used by both user profiles AND auth app IDs. It has its own lifecycle (active → soft-deleted → expired) and its own data model (DeletedAtKey, ExpiresAtKey timestamps).
- The soft-delete logic is already generic — `ApplySoftDeletes` takes `entityType` as a parameter. Making it a separate class formalizes what the code already does.
- This logic has a non-trivial state machine (check grace period, handle inconsistent markers, parse timestamps). It deserves dedicated unit tests.

---

### 3D. Extract `AsyncClientConfigValidator` (from `GetAsyncParams` + validation helpers)

**What to move:**
- `GetAsyncParams()` (lines 598–713, ~116 LOC) — parses async config string, validates container/topic names
- `IsValidBlobContainerName()` (lines 728–740, ~13 LOC) — Azure container name validation
- `IsValidServiceBusTopicName()` (lines 745–758, ~14 LOC) — Azure SB topic name validation
- `_userInformation` dictionary field (line 39)

**New class signature:**
```csharp
public class AsyncClientConfigValidator
{
    private readonly BackendOptions _options;
    private readonly ILogger<AsyncClientConfigValidator> _logger;
    private readonly Dictionary<string, AsyncClientInfo> _cache = new();
    
    public AsyncClientInfo? GetAsyncConfig(string userId, Dictionary<string, string> profileData) { ... }
    
    public static bool IsValidBlobContainerName(string name, out string validated) { ... }
    public static bool IsValidServiceBusTopicName(string name, out string validated) { ... }
}
```

**Why this makes sense:**
- Async configuration parsing is completely independent of user profile storage. It reads a specific field from a profile, parses a comma-separated config string, validates Azure resource names, and caches results.
- The Azure name validation methods (`IsValidBlobContainerName`, `IsValidServiceBusTopicName`) are pure functions with regex — they could be tested independently today if they were accessible.
- The `_userInformation` cache is only used by `GetAsyncParams` — it should live with the method that populates it.

---

### 3E. What remains in `UserProfile` after extractions

```csharp
public class UserProfile : IUserProfileService
{
    private readonly UserConfigLoader _configLoader;
    private readonly SoftDeleteManager _softDeleteManager;
    private readonly AsyncClientConfigValidator _asyncValidator;
    
    // Thread-safe profile/authAppID/suspended dictionaries
    private volatile Dictionary<string, Dictionary<string, string>> userProfiles;
    private volatile List<string> suspendedUserProfiles;
    private volatile Dictionary<string, Dictionary<string, string>> authAppIDs;
    
    // Simple query methods (~120 LOC total):
    public (Dictionary<string, string>, bool, bool) GetUserProfile(string userId) { ... }
    public bool IsUserSuspended(string userId) { ... }
    public bool IsAuthAppIDValid(string? authAppId) { ... }
    public bool IsUserDeleted(string userId) { ... }
    public bool ServiceIsReady() => _configLoader.ServiceIsReady();
    public AsyncClientInfo? GetAsyncParams(string userId) => _asyncValidator.GetAsyncConfig(userId, ...);
}
```

This reduces `UserProfile` from **869 LOC** to **~150 LOC** — a pure repository/query interface.

---

## 4. RequestData.cs (585 LOC) — Separate Data from Behavior

### Why it's a problem
`RequestData` is a data class with **41+ public properties** that also:
1. Has **static service references** (lines 22–26) for SBRequestService, BackupAPIService, UserPriorityService
2. Has **property setters that call external services** (lines 94–123): setting `RequestAPIStatus` calls `BackupAPIService.UpdateStatus()`, setting `SBStatus` calls `SBRequestService.updateStatus()`
3. Implements **both** `IDisposable` and `IAsyncDisposable` with **overlapping cleanup** (7 try-catch blocks)
4. Contains business logic: `CalculateExpiration()` (lines 311–360), `Cleanup()` (lines 362–370)
5. Has a `SkipDispose` flag that can bypass resource cleanup

### 4A. Remove hidden side effects from property setters

**What to change** (lines 94–123):

**Current (hidden side effects):**
```csharp
public RequestAPIStatusEnum RequestAPIStatus
{
    set {
        _requestAPIDocument.status = value;
        if (runAsync) { BackupAPIService!.UpdateStatus(_requestAPIDocument); } // HIDDEN SERVICE CALL
    }
}

public ServiceBusMessageStatusEnum SBStatus
{
    set {
        _sbStatus = value;
        if (runAsync) { SBRequestService?.updateStatus(this); } // HIDDEN SERVICE CALL
    }
}
```

**Proposed (explicit methods):**
```csharp
// Simple auto-properties with no side effects
public RequestAPIStatusEnum RequestAPIStatus { get; set; }
public ServiceBusMessageStatusEnum SBStatus { get; set; }
```

**Callers change from:**
```csharp
request.SBStatus = ServiceBusMessageStatusEnum.Queued;
```

**To:**
```csharp
request.SBStatus = ServiceBusMessageStatusEnum.Queued;
_sbService.UpdateStatus(request);  // Explicit — visible, testable, debuggable
```

**Why this makes sense:**
- Every caller that sets `SBStatus` thinks they're setting a property but they're actually making a Service Bus API call. This makes debugging impossible — when you see `request.SBStatus = X`, nothing in that line suggests network I/O.
- The `if (runAsync)` guard inside the setter means the side effect is conditional — making it even harder to reason about.
- Explicit service calls are grep-able: `_sbService.UpdateStatus` shows up in search results; `request.SBStatus =` doesn't reveal its side effects.

**Call sites to update** (search for `SBStatus =` and `RequestAPIStatus =`):
- `server.cs` line 519: `rd.SBStatus = ServiceBusMessageStatusEnum.Queued`
- `ProxyWorker.cs` multiple locations in error/success paths  
- `RequestLifecycleManager.cs` status transitions
- `AsyncWorker.cs` async status updates

---

### 4B. Eliminate static service references

**What to change** (lines 22–26, 166–175):

**Current:**
```csharp
public static IServiceBusRequestService? SBRequestService { get; private set; }
public static IBackupAPIService? BackupAPIService { get; private set; }
public static IUserPriorityService? UserPriorityService { get; private set; }
public static BackendOptions? BackendOptionsStatic { get; private set; }

public static void InitializeServiceBusRequestService(...) { ... }
```

**Proposed:** Remove all four static properties and the `InitializeServiceBusRequestService` method. Pass services to the code that uses them:

- `Cleanup()` (line 362–370) uses `UserPriorityService` and `BackendOptionsStatic` — move `Cleanup()` to `RequestLifecycleManager` where these services are already available via DI.
- `RequestAPIStatus` setter uses `BackupAPIService` — after 4A, callers pass the service explicitly.
- `SBStatus` setter uses `SBRequestService` — after 4A, callers pass the service explicitly.

**Why this makes sense:**
- Static mutable state makes unit testing impossible — you can't run tests in parallel because they share static references.
- `InitializeServiceBusRequestService` is called once during startup (in `Program.cs`) and sets static references that all `RequestData` instances share. This is a service locator anti-pattern.
- The services are already available via DI in every class that creates or processes `RequestData` — there's no need for static access.

---

### 4C. Simplify the dual disposal pattern

**What to change** (lines 372–584):

**Current:** Two overlapping disposal implementations:
- `Dispose()` (lines 372–462): 6 try-catch blocks for Body, Context.Request.InputStream, Context.Response.OutputStream, Context.Response.Close, OutputStream
- `DisposeAsyncCore()` (lines 477–578): Same 6 try-catch blocks + AsyncWorker disposal

**Proposed:** Consolidate to a single async disposal path:

```csharp
public async ValueTask DisposeAsync()
{
    if (SkipDispose) return;
    
    // Dispose AsyncWorker first (only present in async mode)
    if (asyncWorker != null)
    {
        await asyncWorker.DisposeAsync().ConfigureAwait(false);
        asyncWorker = null;
    }
    
    // Dispose streams in order
    await DisposeStreamSafelyAsync(Body, "body");
    await DisposeStreamSafelyAsync(Context?.Request?.InputStream, "input");
    await DisposeStreamSafelyAsync(Context?.Response?.OutputStream, "output");
    
    // Close response
    try { Context?.Response?.Close(); }
    catch (Exception) { /* log */ }
    
    // Flush and dispose OutputStream if different from context
    if (OutputStream != null && OutputStream != Context?.Response?.OutputStream)
    {
        await DisposeStreamSafelyAsync(OutputStream, "custom output");
    }
    
    Context = null;
    Body = null;
    OutputStream = null;
    
    GC.SuppressFinalize(this);
}

private async ValueTask DisposeStreamSafelyAsync(Stream? stream, string name)
{
    if (stream == null) return;
    try { await stream.DisposeAsync(); }
    catch (Exception) { _logger.LogDebug("Failed to dispose {Name} stream", name); }
}
```

**Why this makes sense:**
- The current code has **14 try-catch blocks** across two disposal methods that do almost the same thing. The `Dispose(bool)` pattern is only needed when you have unmanaged resources — `RequestData` has none.
- The `SkipDispose` flag is checked in 4 places (`Dispose()`, `Dispose(bool)`, `DisposeAsync()`, `DisposeAsyncCore()`) — DRY violation.
- Since the codebase uses `await using (incomingRequest)` (ProxyWorker.cs line 226), only `IAsyncDisposable` is actually called. The synchronous `IDisposable` path is dead code in the happy path.

---

### 4D. Extract `CalculateExpiration` to a utility

**What to move** (lines 311–360, ~50 LOC):

**Current:** `CalculateExpiration` is an instance method on `RequestData` that modifies `ExpiresAt`, `ExpireReason`, and `ExpiresAtString`.

**Proposed:** Make it a static utility or move to `RequestLifecycleManager`:

```csharp
// In RequestLifecycleManager (already exists)
public void SetExpiration(RequestData request, int defaultTTLSecs, string ttlHeaderName)
{
    var (expiresAt, reason) = TTLCalculator.Calculate(
        request.EnqueueTime, request.defaultTimeout, defaultTTLSecs, 
        request.Headers.Get(ttlHeaderName));
    request.ExpiresAt = expiresAt;
    request.ExpireReason = reason;
    request.ExpiresAtString = expiresAt.ToString("o");
}
```

**Why this makes sense:**
- `CalculateExpiration` is business logic (TTL parsing with 4 different formats: relative seconds, absolute Unix seconds, ISO 8601, default fallback). It doesn't need to be on the data class.
- Moving it to `RequestLifecycleManager` puts it next to `ValidateRequestNotExpired` — the code that consumes the expiration.
- The method is called from `server.cs` line 474: `rd.CalculateExpiration(_options.DefaultTTLSecs, _options.TTLHeader)`. After moving, it becomes `_lifecycleManager.SetExpiration(rd, ...)`.

---

## 5. Execution Order & Dependencies

### Recommended order (each step is independently shippable):

| Step | What | Risk | Dependencies |
|------|------|------|-------------|
| **Step 1** | Extract `RequestValidator` from `server.cs` | Low | None — purely moves inline code to a class |
| **Step 2** | Extract `RequestPriorityCalculator` from `server.cs` | Low | None |
| **Step 3** | Extract `EnqueueDecisionMaker` from `server.cs` | Low | None |
| **Step 4** | Remove `RequestData` property side effects (4A) | Medium | Requires updating all callers of `SBStatus =` and `RequestAPIStatus =` |
| **Step 5** | Eliminate `RequestData` static services (4B) | Medium | Requires moving `Cleanup()` to `RequestLifecycleManager` |
| **Step 6** | Extract `SoftDeleteManager` from `UserProfile` | Low | None — self-contained logic |
| **Step 7** | Extract `UserConfigParser` from `UserProfile` | Low | Depends on Step 6 (uses `ApplySoftDeletes`) |
| **Step 8** | Extract `AsyncClientConfigValidator` from `UserProfile` | Low | None |
| **Step 9** | Extract `UserConfigLoader` from `UserProfile` | Medium | Depends on Steps 6, 7 |
| **Step 10** | Extract `BackendRequestExecutor` from `ProxyWorker` | High | Largest extraction; needs careful testing |
| **Step 11** | Extract `ResponseWriter` from `ProxyWorker` | Medium | Can be done with Step 10 |
| **Step 12** | Extract `ErrorResponseWriter` from `ProxyWorker` | Low | Smaller, self-contained |
| **Step 13** | Simplify `RequestData` disposal (4C) | Medium | Do after Steps 4-5 |
| **Step 14** | Move `CalculateExpiration` (4D) | Low | Do after Step 5 |

### Key principle: each step produces a working system
Every extraction should pass all existing tests and maintain the same external behavior. No step should require the next step to be functional.

---

## 6. Expected Outcomes

### Before
| Metric | Value |
|--------|-------|
| Largest method | `ProxyToBackEndAsync`: 560 LOC |
| Largest class | `ProxyWorker`: 1,756 LOC |
| Max constructor params | `ProxyWorker`: 16 |
| Untestable code | ~2,000 LOC (in God methods) |
| Max nesting depth | 7+ levels in `Server.Run()` |

### After
| Metric | Value |
|--------|-------|
| Largest method | ~80 LOC (within `BackendRequestExecutor`) |
| Largest class | ~500 LOC (`BackendRequestExecutor`) |
| Max constructor params | 8 |
| Untestable code | ~0 LOC (all classes DI-injected) |
| Max nesting depth | 3-4 levels |

### New files created
| File | LOC | Extracted from |
|------|-----|---------------|
| `Proxy/BackendRequestExecutor.cs` | ~550 | ProxyWorker.cs |
| `Proxy/ResponseWriter.cs` | ~200 | ProxyWorker.cs |
| `Proxy/ErrorResponseWriter.cs` | ~130 | ProxyWorker.cs |
| `RequestValidator.cs` | ~120 | server.cs |
| `RequestPriorityCalculator.cs` | ~90 | server.cs |
| `EnqueueDecisionMaker.cs` | ~60 | server.cs |
| `AsyncRequestConfigurator.cs` | ~30 | server.cs |
| `User/UserConfigLoader.cs` | ~210 | UserProfile.cs |
| `User/UserConfigParser.cs` | ~140 | UserProfile.cs |
| `User/SoftDeleteManager.cs` | ~150 | UserProfile.cs |
| `User/AsyncClientConfigValidator.cs` | ~150 | UserProfile.cs |

**Net LOC change:** ~0 (refactoring, not removal). The same code moves to focused, testable classes.
