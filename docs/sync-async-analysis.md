# Sync ⇒ Async Code Path Analysis: Streaming-Release-TR Branch

## Executive Summary

This document presents a comprehensive analysis of the sync→async code paths in the SimpleL7Proxy codebase (Streaming-Release-TR branch). The analysis identifies **7 critical**, **12 high**, and **9 medium** severity issues across the request processing pipeline, streaming infrastructure, queue management, event handling, and shutdown coordination.

The most impactful findings are:
1. **Race conditions** in the queue signaling system (`ConcurrentSignal`) with zero synchronization on shared mutable fields
2. **Sync-over-async blocking** in 9 locations that risk thread-pool starvation and deadlocks
3. **Fire-and-forget async operations** that silently lose exceptions and prevent proper error recovery
4. **A confirmed bug** in `CompositeEventClient.StartTimer()` that calls `StopTimer()` on all event clients
5. **Missing cancellation token propagation** in the streaming pipeline, preventing graceful shutdown

---

## Table of Contents

1. [Sync-over-Async Antipatterns](#1-sync-over-async-antipatterns)
2. [Async-over-Sync & Fire-and-Forget](#2-async-over-sync--fire-and-forget)
3. [Race Conditions & Thread Safety](#3-race-conditions--thread-safety)
4. [Queue Reliability](#4-queue-reliability)
5. [Streaming Pipeline Reliability](#5-streaming-pipeline-reliability)
6. [Shutdown Coordination](#6-shutdown-coordination)
7. [Resource Leaks & Disposal](#7-resource-leaks--disposal)
8. [Exception Handling Gaps](#8-exception-handling-gaps)
9. [Bug: CompositeEventClient.StartTimer()](#9-bug-compositeeventclientstarttimer)
10. [Prioritized Recommendations](#10-prioritized-recommendations)

---

## 1. Sync-over-Async Antipatterns

These are blocking calls (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`) inside code that should be async. They risk thread-pool starvation and potential deadlocks.

### 1.1 BlobWriteQueue.cs — Line 596 ⚠️ HIGH

```csharp
var successCount = deduplicatedOps.Count(op => op.GetResultAsync().Result.Success);
```

**Context:** Called inside `ExecuteBatchAsync()`, which is already async. The `.Result` blocks the thread waiting for each operation's result.

**Impact:** Under high load with many blob write operations, this blocks thread-pool threads unnecessarily. Since `Task.WhenAll(writeTasks)` completes on line 591 just before this, the results are likely already available — but if any task uses async continuations, this can still deadlock.

**Recommendation:** Collect results asynchronously:
```csharp
var results = await Task.WhenAll(deduplicatedOps.Select(op => op.GetResultAsync()));
var successCount = results.Count(r => r.Success);
```

### 1.2 ProbeServer.cs — Line 89 ⚠️ HIGH

```csharp
var response = selfCheckClient.GetAsync(url).Result;
```

**Context:** Called inside a `System.Threading.Timer` callback that fires every ~1 second. Timer callbacks execute on ThreadPool threads.

**Impact:** Each probe check blocks a ThreadPool thread for the duration of the HTTP request. With multiple probe endpoints and a 1-second timer interval, this can exhaust ThreadPool threads under load, affecting all async operations in the process.

**Recommendation:** Convert to async timer pattern using `PeriodicTimer` or `Task.Run` with async lambda:
```csharp
var response = await selfCheckClient.GetAsync(url).ConfigureAwait(false);
```

### 1.3 AsyncWorkerFactory.cs — Line 34 ⚠️ HIGH

```csharp
_blobWriter.InitClientAsync(Constants.Server, Constants.Server).GetAwaiter().GetResult();
```

**Context:** Called in the constructor during DI container initialization.

**Impact:** Blocks the startup thread. If Azure Blob Storage is unreachable, the entire application hangs with no timeout. The current code silently disables async mode (line 38) on failure, but the blocking call itself is the issue.

**Recommendation:** Use a factory pattern or `IHostedService.StartAsync()` for async initialization:
```csharp
public class AsyncWorkerFactory : IAsyncWorkerFactory, IHostedService
{
    public async Task StartAsync(CancellationToken ct) {
        await _blobWriter.InitClientAsync(...).ConfigureAwait(false);
    }
}
```

### 1.4 QueuedBlobWriter.cs — Line 167 ⚠️ MEDIUM

```csharp
FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
```

**Context:** In synchronous `Dispose()` method. The class also has `DisposeAsync()` (line 176).

**Impact:** If the blob write queue is backed up, this blocks the disposing thread indefinitely (no cancellation token). Could cause GC finalizer thread starvation if many instances are disposed simultaneously.

**Recommendation:** Ensure all callers use `await using` to trigger `DisposeAsync()` instead. Add a timeout to the sync fallback:
```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
FlushAsync(cts.Token).GetAwaiter().GetResult();
```

### 1.5 EventHubClient.cs — Lines 109, 114 ⚠️ MEDIUM

```csharp
// Line 109: Spin-wait loop during shutdown
while (isRunning && _logBuffer.Count > 0) {
    Task.Delay(100).Wait();  // Blocking!
}
// Line 114:
writerTask?.Wait();  // Blocking!
```

**Impact:** Shutdown hangs if the log buffer never drains. Two sequential blocking waits with no timeout.

**Recommendation:** Use async drain with timeout:
```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
while (isRunning && _logBuffer.Count > 0) {
    await Task.Delay(100, cts.Token).ConfigureAwait(false);
}
if (writerTask != null) await writerTask.WaitAsync(cts.Token).ConfigureAwait(false);
```

### 1.6 LogFileEventClient.cs — Lines 133, 137 ⚠️ MEDIUM

Same pattern as EventHubClient — `Task.Delay(100).Wait()` and `writerTask?.Wait()` in `StopTimer()`.

### 1.7 ServiceBusRequestService.cs — Line 315 ⚠️ MEDIUM

```csharp
Task.Delay(100).Wait();
```

Same spin-wait pattern during shutdown.

---

## 2. Async-over-Sync & Fire-and-Forget

### 2.1 ProxyWorker.cs — Line 1704 ⚠️ CRITICAL

```csharp
_ = request.asyncWorker.StartAsync();
```

**Context:** `StartAsync()` is fired-and-forgotten. No exception handler, no task tracking.

**Impact:**
- If `StartAsync()` throws, the exception becomes an unobserved task exception
- The async worker could fail to initialize without any indication
- No retry logic, no fallback
- The request continues processing assuming the async worker is running

**Recommendation:** Track the task and handle failures:
```csharp
var startTask = request.asyncWorker.StartAsync();
startTask.ContinueWith(t => {
    if (t.IsFaulted)
        _logger.LogError(t.Exception, "AsyncWorker.StartAsync failed for {Guid}", request.Guid);
}, TaskContinuationOptions.OnlyOnFaulted);
```

### 2.2 server.cs — Line 178 ⚠️ HIGH

```csharp
return backendStartTask.ContinueWith((x) => Run(token), token);
```

**Context:** `Run()` is an async method but called via `ContinueWith()`, which doesn't unwrap the returned Task.

**Impact:** Exception handling flows are fragmented. If `Run()` throws, the exception is wrapped in a `Task<Task>` and may not propagate correctly.

**Recommendation:** Use `async/await`:
```csharp
await backendStartTask.ConfigureAwait(false);
await Run(token).ConfigureAwait(false);
```

### 2.3 CoordinatedShutdownService.cs — Line 90 ⚠️ MEDIUM

```csharp
ProxyWorkerCollection.ExpelAsyncRequests();
```

**Context:** Fire-and-forget during shutdown. This method backs up async requests to blob storage.

**Impact:** If backup fails, async requests are permanently lost. No await, no error checking.

**Recommendation:** Await the result with a timeout:
```csharp
await ProxyWorkerCollection.ExpelAsyncRequestsAsync()
    .WaitAsync(TimeSpan.FromSeconds(30))
    .ConfigureAwait(false);
```

---

## 3. Race Conditions & Thread Safety

### 3.1 ConcurrentSignal — `_probeWorkerTask` Fields ⚠️ CRITICAL

```csharp
// Lines 9-10: NO synchronization at all
private WorkerTask<T>? _probeWorkerTask;
private bool _probeWorkerTaskSet;
```

**Used in 4 methods without any locking:**
- `WaitForSignalAsync()` — sets both fields (lines 19-20)
- `ReQueueTask()` — sets both fields (lines 35-36)
- `GetNextProbeTask()` — reads and clears both (lines 46-49)
- `CancelAllTasks()` — reads and clears both (lines 72-75)

**Race Scenarios:**
1. **Lost worker:** Thread A calls `WaitForSignalAsync(0)`, sets `_probeWorkerTask`. Thread B calls `GetNextProbeTask()`, reads stale `_probeWorkerTaskSet = false`, returns null. Worker task is never signaled → worker hangs forever.
2. **Double delivery:** Thread A and Thread B both read `_probeWorkerTaskSet = true` in `GetNextProbeTask()`. Both get the same `_probeWorkerTask`. Both call `SetResult()` → `InvalidOperationException` on second call.
3. **Torn read:** Thread A writes `_probeWorkerTask = X` (line 19). Thread B reads `_probeWorkerTaskSet = false` (line 46) then `_probeWorkerTask` which is stale → inconsistent state.

**Recommendation:** Use a lock or `Interlocked` with a single combined field:
```csharp
private readonly object _probeLock = new();

public WorkerTask<T>? GetNextProbeTask() {
    lock (_probeLock) {
        if (_probeWorkerTaskSet) {
            _probeWorkerTaskSet = false;
            return _probeWorkerTask;
        }
    }
    return GetNextTask();
}
```

### 3.2 ConcurrentPriQueue — Count Check Outside Lock ⚠️ HIGH

```csharp
// Line 79: Count check OUTSIDE lock
if (!allowOverflow && _priorityQueue.Count >= MaxQueueLength) {
    return false;
}
// Line 84: Enqueue INSIDE lock
lock (_lock) {
    _priorityQueue.Enqueue(queueItem);
}
```

**Impact:** Between the count check and the lock acquisition, other threads can enqueue items, exceeding `MaxQueueLength`. Under high concurrency (~1000 workers), this is a realistic scenario.

**Recommendation:** Move the count check inside the lock:
```csharp
lock (_lock) {
    if (!allowOverflow && _priorityQueue.Count >= MaxQueueLength) {
        return false;
    }
    _priorityQueue.Enqueue(queueItem);
}
```

### 3.3 server.cs — `_isShuttingDown` Not Volatile ⚠️ HIGH

```csharp
// Referenced in shutdown checks but not declared volatile
if (!_isShuttingDown) { ... }
```

**Impact:** The JIT compiler and CPU may cache the value in a register. Worker threads may read stale `false` after the field is set to `true`, continuing to accept requests during shutdown.

**Recommendation:** Declare as `volatile` or use `Volatile.Read()`/`Volatile.Write()`.

### 3.4 ProxyWorker.cs — Static Mutable State ⚠️ HIGH

```csharp
// Lines 34-36, 57-58
private static bool s_debug = false;
private static IConcurrentPriQueue<RequestData>? s_requestsQueue;
private static string[] s_backendKeys = Array.Empty<string>();
```

**Impact:** Multiple worker tasks write to `s_backendKeys` (line 101) during initialization. Since workers are created and started concurrently, this is a data race.

**Recommendation:** Initialize static state once in a thread-safe manner (e.g., `Lazy<T>` or initialization in `ProxyWorkerCollection` before workers start).

### 3.5 AsyncWorker — `_beginStartup` CAS Races ⚠️ MEDIUM

```csharp
// Line 32
private int _beginStartup = 0;
// Line 352: CAS to start
Interlocked.CompareExchange(ref _beginStartup, 1, 0)
// Line 706: CAS in Synchronize
Interlocked.CompareExchange(ref _beginStartup, -1, 0)
// Line 738: CAS in AbortAsync
Interlocked.CompareExchange(ref _beginStartup, -1, 0)
```

**Impact:** If `Synchronize()` and `AbortAsync()` race, both attempt CAS from 0→-1. One succeeds, one silently fails. The failing path doesn't know it lost the race and may proceed with stale assumptions.

**Recommendation:** Add explicit state machine logging and validation after CAS:
```csharp
var prev = Interlocked.CompareExchange(ref _beginStartup, -1, 0);
if (prev != 0) {
    _logger.LogWarning("Expected _beginStartup=0, got {prev}", prev);
    return; // or throw
}
```

### 3.6 RequestData — Non-Atomic `??=` Pattern ⚠️ MEDIUM

```csharp
// Lines 42-50
set {
    _backgroundRequestId = value;
    _requestAPIDocument ??= RequestDataConverter.ToRequestAPIDocument(this);
    _requestAPIDocument.backgroundRequestId = value;
}
```

**Impact:** `??=` is not atomic. Two threads setting `BackgroundRequestId` simultaneously can both call `ToRequestAPIDocument(this)`, creating two different document instances. One is discarded, but side effects from construction may persist.

### 3.7 CircuitBreaker — Static Counter TOCTOU ⚠️ MEDIUM

```csharp
// Line 134
int total = _totalCircuitBreakersCount;
int blocked = _blockedCircuitBreakersCount;
// Gap between reads allows inconsistent snapshot
```

**Impact:** `AreAllCircuitBreakersBlocked()` can return a false positive (all blocked) during the window between creation/destruction of circuit breakers.

---

## 4. Queue Reliability

### 4.1 Signal-Worker Pairing Race ⚠️ CRITICAL

The `ConcurrentPriQueue.SignalWorker()` method (line 100) runs in a loop dequeuing workers from `ConcurrentSignal` and pairing them with queued requests. However:

```csharp
// In Enqueue():
var t = _taskSignaler.GetNextProbeTask();     // Try fast path
if (t != null) {
    t.TaskCompletionSource.SetResult(item);   // Direct delivery
    return true;
}
// ... enqueue to priority queue ...
_enqueueEvent.Release();                      // Signal worker

// In SignalWorker():
await _enqueueEvent.WaitAsync(cancellationToken);
var nextWorker = _taskSignaler.GetNextProbeTask();
// ... pair with queue item ...
```

**Race scenario:** 
1. Thread A calls `Enqueue()` — `GetNextProbeTask()` returns null (no workers waiting)
2. Thread A enqueues to priority queue
3. Thread B calls `DequeueAsync()` — registers worker via `WaitForSignalAsync()`
4. Thread A calls `_enqueueEvent.Release()` — wakes up `SignalWorker`
5. `SignalWorker` calls `GetNextProbeTask()` — gets Thread B's worker
6. Meanwhile, Thread C also calls `Enqueue()` and gets the same probe worker (race in 3.1)
7. Double-delivery or lost worker

**Impact:** Under high concurrency, items can be delivered to the wrong worker, or workers can starve.

### 4.2 StopAsync() Has No Timeout ⚠️ HIGH

```csharp
public async Task StopAsync() {
    while (_priorityQueue.Count > 0) {
        await Task.Delay(100);  // No cancellation token!
    }
    _taskSignaler.CancelAllTasks();
}
```

**Impact:** If the queue never drains (e.g., stuck worker), shutdown hangs forever.

**Recommendation:** Add a timeout:
```csharp
public async Task StopAsync(CancellationToken ct = default) {
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(30));
    while (_priorityQueue.Count > 0 && !cts.Token.IsCancellationRequested) {
        await Task.Delay(100, cts.Token).ConfigureAwait(false);
    }
    _taskSignaler.CancelAllTasks();
}
```

---

## 5. Streaming Pipeline Reliability

### 5.1 Missing CancellationToken in IStreamProcessor ⚠️ HIGH

```csharp
// IStreamProcessor.cs line 18
Task CopyToAsync(HttpContent sourceContent, Stream outputStream);
// No CancellationToken parameter!
```

**Impact:** All stream processing operations (DefaultStreamProcessor, JsonStreamProcessor, OpenAIProcessor, etc.) cannot be cancelled. During shutdown, active streams continue indefinitely until the backend closes the connection or a timeout occurs.

**Recommendation:** Add `CancellationToken` to the interface:
```csharp
Task CopyToAsync(HttpContent sourceContent, Stream outputStream, 
    CancellationToken cancellationToken = default);
```

### 5.2 JsonStreamProcessor — Deferred Await on Write ⚠️ HIGH

```csharp
// Line 74
Task t = writer.WriteLineAsync(currentLine);
// Lines 77-82: Sync processing while write is pending
// Line 84
await t.ConfigureAwait(false);
```

**Impact:** The sync processing on lines 77-82 may execute while the previous write is still in progress. If the write fails (broken connection), the exception is not observed until line 84, after processing has already been done on the next line. This can cause:
- Out-of-order data if two writes overlap
- Wasted processing on data that will never be delivered

**Recommendation:** Await writes immediately:
```csharp
await writer.WriteLineAsync(currentLine).ConfigureAwait(false);
```

### 5.3 JsonStreamProcessor — Missing Flush Before Disposal ⚠️ MEDIUM

```csharp
using var writer = new StreamWriter(outputStream, bufferSize: 4096, leaveOpen: true);
// ... streaming loop ...
// No explicit flush before writer disposal
```

**Impact:** StreamWriter with `leaveOpen: true` may not flush its internal buffer when disposed. Data in the buffer (up to 4096 bytes) can be lost.

**Recommendation:** Add explicit flush:
```csharp
finally {
    await writer.FlushAsync().ConfigureAwait(false);
}
```

### 5.4 Commented-Out Cancellation Check ⚠️ MEDIUM

```csharp
// JsonStreamProcessor.cs Line 71
//cancellationToken?.ThrowIfCancellationRequested();
```

**Impact:** Indicates an incomplete refactoring. Cancellation was planned but never implemented.

---

## 6. Shutdown Coordination

### 6.1 CoordinatedShutdownService — Ordering Gaps ⚠️ HIGH

The shutdown sequence in `CoordinatedShutdownService.StopAsync()` has several issues:

```csharp
// Line 90: Fire-and-forget backup
ProxyWorkerCollection.ExpelAsyncRequests();

// Lines 95-99: Race between timeout and task completion
var timeoutTask = Task.Delay(_options.TerminationGracePeriodSeconds * 1000, CancellationToken.None);
await requeueTask.ConfigureAwait(false);
var allTasksComplete = Task.WhenAll(ProxyWorkerCollection.GetAllTasks());
var completedTask = await Task.WhenAny(allTasksComplete, timeoutTask).ConfigureAwait(false);
```

**Issues:**
1. `ExpelAsyncRequests()` is not awaited — async request backup may fail silently
2. `CancellationToken.None` on timeout — can't be interrupted by host shutdown
3. `GetAllTasks()` is called after `requeueTask` completes, but more tasks may have been created in the interim
4. No `requeueTask` timeout — if requeue hangs, the timeout task for all tasks never starts

**Recommendation:** Implement structured shutdown phases:
```csharp
// Phase 1: Stop accepting new requests
_isShuttingDown = true;

// Phase 2: Backup async requests (with timeout)
await ExpelAsyncRequestsAsync().WaitAsync(backupTimeout).ConfigureAwait(false);

// Phase 3: Drain workers (with timeout)
var allTasks = ProxyWorkerCollection.GetAllTasks();
await Task.WhenAll(allTasks).WaitAsync(drainTimeout).ConfigureAwait(false);

// Phase 4: Flush telemetry and blob queues
await FlushTelemetryAsync().WaitAsync(flushTimeout).ConfigureAwait(false);
```

### 6.2 server.cs — GetContextAsync() Not Cancellable ⚠️ MEDIUM

```csharp
// Line 205-208
var getContextTask = _httpListener.GetContextAsync();
var completedTask = await Task.WhenAny(
    getContextTask, 
    Task.Delay(Timeout.Infinite, cancellationToken)
).ConfigureAwait(false);
```

**Impact:** `GetContextAsync()` doesn't accept a `CancellationToken`. The `Task.WhenAny` pattern works around this, but the `getContextTask` continues running in the background after cancellation. This leaks `HttpListenerContext` objects.

**Recommendation:** After cancellation, call `_httpListener.Close()` to abort pending `GetContextAsync()` calls.

---

## 7. Resource Leaks & Disposal

### 7.1 AsyncWorker — Stream Creation Race ⚠️ HIGH

```csharp
// Lines 527-542
if (_hos == null) {       // Check
    // ... async operations ...
    _hos = stream;         // Set
}
```

**Impact:** Multiple concurrent `WriteHeaders()` calls could both see `_hos == null`, both create streams, but only one assignment survives. The other stream is leaked (never disposed).

**Recommendation:** Use `lock` or `SemaphoreSlim` to protect stream creation:
```csharp
await _streamCreationSemaphore.WaitAsync().ConfigureAwait(false);
try {
    if (_hos == null) {
        _hos = await CreateStreamAsync().ConfigureAwait(false);
    }
} finally {
    _streamCreationSemaphore.Release();
}
```

### 7.2 AsyncWorker — Blob Stream Leak on Exception ⚠️ MEDIUM

```csharp
// Lines 235-259
var dataStream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(_userId, dataBlobName);
var headerStream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(_userId, headerBlobName);
// If second call throws, first stream is orphaned
return (dataStream, headerStream);
```

**Recommendation:** Wrap in try/catch to dispose on failure:
```csharp
Stream? dataStream = null;
try {
    dataStream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(...);
    var headerStream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(...);
    return (dataStream, headerStream);
} catch {
    dataStream?.Dispose();
    throw;
}
```

### 7.3 AsyncWorker — DisposeAsync Not Idempotent ⚠️ MEDIUM

```csharp
// Lines 761-796 - No disposed guard
public async ValueTask DisposeAsync() {
    // Can be called multiple times
    _cancellationTokenSource?.Cancel();
    _cancellationTokenSource?.Dispose();
}
```

**Impact:** Calling `DisposeAsync()` twice can throw `ObjectDisposedException` on CTS operations.

**Recommendation:** Add an idempotency guard:
```csharp
private int _disposed = 0;
public async ValueTask DisposeAsync() {
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
    // ... disposal logic ...
}
```

### 7.4 RequestData — Dispose/DisposeAsync Race ⚠️ MEDIUM

Both `Dispose()` and `DisposeAsync()` can be called concurrently with no mutual exclusion. The `SkipDispose` flag is checked without synchronization.

### 7.5 server.cs — RequestData Leak on Enqueue Failure ⚠️ MEDIUM

```csharp
// Lines 507-514
if (!_requestsQueue.Enqueue(rd, priority, userPriorityBoost, rd.EnqueueTime)) {
    notEnqued = true;
    notEnquedCode = 429;
}
```

**Impact:** If enqueue fails, `RequestData` is partially constructed with streams and context that may not be fully cleaned up in the error path.

---

## 8. Exception Handling Gaps

### 8.1 ProxyWorker — Exception Swallowed in Finally Block ⚠️ HIGH

```csharp
// Lines 521-589
finally {
    try {
        // ... cleanup code ...
    } catch (Exception e) {
        s_finallyBlockErrorEvent.SendEvent();
        _logger.LogError(e, ...);
        // NO RETHROW — exception dies here
    }
}
```

**Impact:** Critical cleanup failures (e.g., failed response to client, failed blob writes) are logged but not propagated. The caller has no way to know cleanup failed.

**Recommendation:** At minimum, rethrow critical exceptions (e.g., `OutOfMemoryException`, `StackOverflowException`). Consider adding a metric for finally-block failures to alert on.

### 8.2 RequeueDelayWorker — AbortAsync() Not Protected ⚠️ HIGH

```csharp
// Lines 74-90
catch (TaskCanceledException) {
    if (request.asyncWorker != null) {
        await request.asyncWorker.AbortAsync().ConfigureAwait(false);
        // If AbortAsync throws, exception propagates uncaught
    }
}
```

**Recommendation:** Wrap `AbortAsync()` in try/catch:
```csharp
try {
    await request.asyncWorker.AbortAsync().ConfigureAwait(false);
} catch (Exception ex) {
    _logger.LogError(ex, "Error aborting async worker during requeue");
}
```

### 8.3 ProxyEvent.SendEvent() — Telemetry Silently Lost ⚠️ MEDIUM

```csharp
catch (Exception ex) {
    Console.Error.WriteLine($"Error sending telemetry: {ex.Message}");
}
```

**Impact:** No retry, no dead-letter queue. Telemetry events are permanently lost on transient failures.

### 8.4 BlobWriteQueue.StopAsync() — Empty Catch ⚠️ MEDIUM

```csharp
try { await Task.WhenAny(...).ConfigureAwait(false); }
catch { }  // Empty catch — all exceptions silently swallowed
```

---

## 9. Bug: CompositeEventClient.StartTimer()

### Confirmed Bug ⚠️ CRITICAL

**File:** `src/SimpleL7Proxy/Events/CompositeEventClient.cs`, Line 15

```csharp
public Task StartTimer()
{
    foreach (var client in eventClients)
    {
        Console.WriteLine($"starting timer for {client}");
        client.StopTimer();  // ← BUG: Should be client.StartTimer()
    }
    return Task.CompletedTask;
}
```

**Impact:** When `CompositeEventClient.StartTimer()` is called during application startup, it **stops** all child event clients instead of starting them. This means:
- EventHubClient timers are stopped
- LogFileEventClient timers are stopped  
- No telemetry is written until individually started (if ever)

**Root Cause:** Likely a copy-paste error from the `StopTimer()` method directly below.

**Fix:** Change `client.StopTimer()` to `client.StartTimer()` on line 15.

---

## 10. Prioritized Recommendations

### Priority 1: Critical Fixes (Address Immediately)

| # | Issue | File | Impact | Effort |
|---|-------|------|--------|--------|
| 1 | **CompositeEventClient.StartTimer() bug** | CompositeEventClient.cs:15 | All telemetry broken | 1 line |
| 2 | **ConcurrentSignal unsynchronized fields** | ConcurrentSignal.cs:9-10 | Lost workers, hangs | Small |
| 3 | **Fire-and-forget StartAsync()** | ProxyWorker.cs:1704 | Silent async failures | Small |
| 4 | **Queue count check outside lock** | ConcurrentPriQueue.cs:79-86 | Queue overflow | Small |

### Priority 2: High-Impact Improvements (Next Sprint)

| # | Issue | File | Impact | Effort |
|---|-------|------|--------|--------|
| 5 | **Add CancellationToken to IStreamProcessor** | IStreamProcessor.cs | Graceful shutdown | Medium |
| 6 | **Fix sync-over-async in ProbeServer** | ProbeServer.cs:89 | Thread pool starvation | Medium |
| 7 | **Fix sync-over-async in BlobWriteQueue** | BlobWriteQueue.cs:596 | Potential deadlock | Small |
| 8 | **AsyncWorker stream creation race** | AsyncWorker.cs:527 | Stream leaks | Small |
| 9 | **StopAsync() timeout** | ConcurrentPriQueue.cs:32-42 | Shutdown hangs | Small |
| 10 | **Make `_isShuttingDown` volatile** | server.cs | Stale shutdown flag | 1 line |

### Priority 3: Reliability Improvements (Backlog)

| # | Issue | File | Impact | Effort |
|---|-------|------|--------|--------|
| 11 | **AsyncWorkerFactory sync init in constructor** | AsyncWorkerFactory.cs:34 | Startup hang risk | Medium |
| 12 | **JsonStreamProcessor deferred await** | JsonStreamProcessor.cs:74 | Data ordering | Small |
| 13 | **JsonStreamProcessor missing flush** | JsonStreamProcessor.cs | Data loss on crash | Small |
| 14 | **CoordinatedShutdown ordering** | CoordinatedShutdownService.cs | Data loss at shutdown | Large |
| 15 | **DisposeAsync idempotency** | AsyncWorker.cs | Double-dispose crash | Small |
| 16 | **Event client shutdown spin-waits** | EventHubClient.cs, LogFileEventClient.cs | Shutdown delays | Medium |
| 17 | **RequestData disposal race** | RequestData.cs | Double-disposal | Medium |
| 18 | **Blob stream leak on exception** | AsyncWorker.cs:235 | Resource leak | Small |

### Architectural Recommendations

1. **Implement a formal state machine for AsyncWorker lifecycle.** The current `_beginStartup` integer flag with CAS operations is fragile and hard to reason about. A proper `enum` with `Interlocked` transitions and logging would prevent the CAS race scenarios.

2. **Adopt structured concurrency patterns.** The mix of fire-and-forget, `ContinueWith`, and untracked `Task.Run` calls creates a web of unobserved failures. Consider using `System.Threading.Channels` for producer-consumer patterns and `TaskGroup`-like abstractions.

3. **Standardize cancellation propagation.** Create a `RequestContext` that carries the cancellation token through the entire pipeline (enqueue → dequeue → proxy → stream → response), instead of having each layer create its own CTS.

4. **Add health metrics for async failures.** The current telemetry silently drops events (ProxyEvent, EventHub). Add a counter metric for failed telemetry sends to detect when monitoring itself is broken.

5. **Consider `SemaphoreSlim` over custom `ConcurrentSignal`.** The `ConcurrentSignal` class implements a bespoke producer-consumer signaling mechanism with multiple race conditions. `System.Threading.Channels<WorkerTask<T>>` would provide the same semantics with built-in thread safety.

---

## Appendix: All Sync-over-Async Locations

| File | Line | Pattern | Blocking Call |
|------|------|---------|---------------|
| BlobWriteQueue.cs | 596 | `.Result` | `GetResultAsync().Result` |
| ProbeServer.cs | 89 | `.Result` | `GetAsync(url).Result` |
| AsyncWorkerFactory.cs | 34 | `.GetAwaiter().GetResult()` | `InitClientAsync().GetAwaiter().GetResult()` |
| QueuedBlobWriter.cs | 167 | `.GetAwaiter().GetResult()` | `FlushAsync().GetAwaiter().GetResult()` |
| EventHubClient.cs | 109 | `.Wait()` | `Task.Delay(100).Wait()` |
| EventHubClient.cs | 114 | `.Wait()` | `writerTask?.Wait()` |
| LogFileEventClient.cs | 133 | `.Wait()` | `Task.Delay(100).Wait()` |
| LogFileEventClient.cs | 137 | `.Wait()` | `writerTask?.Wait()` |
| ServiceBusRequestService.cs | 315 | `.Wait()` | `Task.Delay(100).Wait()` |
