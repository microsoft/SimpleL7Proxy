# Race Condition Analysis — `streaming-release-tr` Branch

> **Analysis Date:** 2026-02-13  
> **Scope:** All shared-state, concurrent, and multithreaded components in `src/SimpleL7Proxy`  
> **Goal:** Identify race conditions, propose fixes — no code changes in this document.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Critical Race Conditions](#2-critical-race-conditions)
3. [Moderate Race Conditions](#3-moderate-race-conditions)
4. [Low-Risk Race Conditions](#4-low-risk-race-conditions)
5. [Design-Level Concerns](#5-design-level-concerns)
6. [Summary Matrix](#6-summary-matrix)

---

## 1. Executive Summary

The codebase is a high-throughput L7 reverse proxy processing ~1000 concurrent tasks with multiple worker threads dequeuing from a priority queue, proxying to backends, and optionally streaming to Azure Blob Storage for async requests.

The analysis identified **6 critical**, **5 moderate**, and **4 low-risk** race conditions. The most severe issues involve:

- **Static mutable `ProxyEvent` objects shared across workers** (data corruption under concurrent access)
- **`PriorityQueue<T>` accessed outside its lock** (list corruption)
- **`ConcurrentSignal<T>` probe worker registration** (lost worker tasks)
- **`_activeHosts` reference swap without synchronization** (torn reads)
- **`RequestData` properties mutated concurrently** between ProxyWorker and AsyncWorker

---

## 2. Critical Race Conditions

### RC-1: Static `ProxyEvent` Reuse Across Concurrent Workers

**Files:** `ProxyWorker.cs:61-62`  
**Shared State:** `s_finallyBlockErrorEvent`, `s_backendRequestAttemptEvent` — both are `static readonly ProxyEvent` instances.

```csharp
private static readonly ProxyEvent s_finallyBlockErrorEvent = new ProxyEvent(30);
private static readonly ProxyEvent s_backendRequestAttemptEvent = new ProxyEvent(25);
```

**The Race:** These static objects are reused by ALL workers (lines 566–588). When Worker A enters the `finally` block's `catch` and calls `s_finallyBlockErrorEvent.Clear()`, then sets properties, Worker B doing the same thing concurrently will:
1. Clear Worker A's data before it calls `SendEvent()`.
2. Mix its own properties with Worker A's data.

While `ProxyEvent` extends `ConcurrentDictionary` (so individual Add/Set won't crash), the composite operation `Clear() → set fields → SendEvent()` is **not atomic**. The resulting telemetry event will be a corrupted mix of two workers' data.

**Impact:** Corrupted telemetry events during error conditions. Under high error rates (circuit breaker tripping), this produces unreliable diagnostics.

**Proposal:** Replace static pre-allocated `ProxyEvent` instances with per-call `new ProxyEvent(capacity)` allocations. The allocation cost is negligible vs. the I/O of `SendEvent()`. Alternatively, use a `[ThreadStatic]` attribute or `ThreadLocal<ProxyEvent>` to keep one instance per thread.

---

### RC-2: `PriorityQueue<T>._items` — List Accessed Without Consistent Locking

**File:** `Queue/PriorityQueue.cs:7, 13, 31`  
**Shared State:** `_items` (a `List<PriorityQueueItem<T>>`) and the static `itemCounter`.

```csharp
public readonly List<PriorityQueueItem<T>> _items = new List<PriorityQueueItem<T>>();
public static int itemCounter = 0;
```

**The Race:**  
- `ConcurrentPriQueue.Enqueue()` (line 84–87) acquires `_lock` to call `_priorityQueue.Enqueue()`.
- `ConcurrentPriQueue.SignalWorker()` (line 113–125) acquires `_lock` to call `_priorityQueue.Dequeue()`.
- **BUT** `ConcurrentPriQueue.thrdSafeCount` (line 50) reads `_priorityQueue.Count` which reads `itemCounter` — this is fine because `itemCounter` uses `Interlocked`.
- **HOWEVER** in `ConcurrentPriQueue.Enqueue()` (line 79): `_priorityQueue.Count >= MaxQueueLength` is read **outside** the lock. Between this check and the enqueue inside the lock, another thread can enqueue, causing the queue to exceed `MaxQueueLength`.

Additionally, `PriorityQueue.refItem` (line 31) is a **static mutable field** used by `Dequeue()`:
```csharp
static PriorityQueueItem<T> refItem = new PriorityQueueItem<T>(default!, 0, 1, DateTime.MinValue);
```
Note: The `default!` (null-forgiving) is used in the actual source for the generic `T Item` property, which works because the item is only used for `BinarySearch` comparison and is never dereferenced. `refItem.UpdateForLookup(priority)` is called inside the lock in `SignalWorker`, so it's protected there. But this static instance is shared across ALL `PriorityQueue<T>` instances of the same generic type — a design smell even if currently safe with only one instance.

**Impact:** The queue may exceed `MaxQueueLength` by the number of concurrent enqueue operations (up to `Workers` count). Under load this is `~1000` extra items beyond the limit.

**Proposal:**  
1. Move the count check inside the lock, or use a separate `Interlocked`-based count that's decremented atomically with enqueue.
2. Make `refItem` an instance field instead of static.

---

### RC-3: `ConcurrentSignal<T>` Probe Worker — Non-Atomic Read/Write of `_probeWorkerTask`

**File:** `Queue/ConcurrentSignal.cs:9-10, 17-21, 44-52`

```csharp
private WorkerTask<T>? _probeWorkerTask;
private bool _probeWorkerTaskSet;
```

**The Race:** `_probeWorkerTask` and `_probeWorkerTaskSet` are read and written without any synchronization:
- `WaitForSignalAsync()` (line 17-21) sets both when `priority == 0`.
- `GetNextProbeTask()` (line 44-52) reads `_probeWorkerTaskSet`, then reads `_probeWorkerTask`.
- `Enqueue()` in `ConcurrentPriQueue` (line 59) calls `GetNextProbeTask()` **without any lock** (the "lock-free fast path").
- `ReQueueTask()` (line 32-41) also writes both fields.

Scenario: Thread A (probe worker) calls `WaitForSignalAsync(0)` which sets `_probeWorkerTask = workerTask` then `_probeWorkerTaskSet = true`. Thread B calls `GetNextProbeTask()` which reads `_probeWorkerTaskSet = true` then sets it to `false` and reads `_probeWorkerTask`. Due to memory reordering on weak-memory architectures (ARM, etc.), Thread B might see `_probeWorkerTaskSet = true` but still read the **old** `_probeWorkerTask` value (null or previous task).

Similarly, `_probeWorkerTaskSet = false` in `GetNextProbeTask()` is not atomic with reading `_probeWorkerTask`. Two concurrent callers to `GetNextProbeTask()` could both see `_probeWorkerTaskSet = true` and both try to use the same task, leading to `SetResult` being called twice (which throws `InvalidOperationException`).

**Impact:** Lost probe worker tasks, probe worker stuck forever waiting, or `InvalidOperationException` on double-completion.

**Proposal:** Use `Interlocked.Exchange` on a single reference field instead of a separate boolean flag:
```csharp
public WorkerTask<T>? GetNextProbeTask()
{
    var task = Interlocked.Exchange(ref _probeWorkerTask, null);
    return task ?? GetNextTask();
}
```

---

### RC-4: `Backends._activeHosts` Reference Swap Without Memory Barrier

**File:** `Backend/Backends.cs:26, 130, 398`

```csharp
private List<BaseHostHealth> _activeHosts;
// ...
public List<BaseHostHealth> GetActiveHosts() => _activeHosts;
// ...
_activeHosts = newActiveHosts; // In FilterActiveHosts()
```

**The Race:** `_activeHosts` is written by the backend poller thread (in `FilterActiveHosts()`) and read concurrently by all proxy worker threads (via `GetActiveHosts()`, `ActiveHostCount()`, and `IteratorFactory`). The reference assignment is atomic in .NET, but without `volatile` or `Interlocked`, there's no guaranteed memory barrier — worker threads may read a stale reference indefinitely on some CPU architectures.

More critically, the `List<BaseHostHealth>` itself is a mutable collection. Although `FilterActiveHosts()` creates a new list rather than modifying in-place (which is good), calling code like:
```csharp
var activeHosts = _backends.GetActiveHosts();
foreach (var host in activeHosts) { ... }
```
could iterate a list that `FilterActiveHosts()` has already replaced. This is actually **safe** (the old list remains valid), but the lack of a memory barrier means threads may not see the updated list promptly.

**Impact:** Stale host lists causing suboptimal routing. Under host churn, a worker could continue trying failed/removed hosts.

**Proposal:** Mark `_activeHosts` as `volatile`, or use `Volatile.Read`/`Volatile.Write` at access points.

---

### RC-5: `RequestData` Properties Mutated Concurrently by ProxyWorker and AsyncWorker

**Files:** `RequestData.cs`, `Proxy/AsyncWorker.cs`, `Proxy/ProxyWorker.cs`

**The Race:** When an async request triggers (after `AsyncTimeout`), the `AsyncWorker.StartAsync()` runs on a different thread (via `Task.Run` in `SetupAsyncWorkerAndTimeout`). Meanwhile, the ProxyWorker continues processing the same `RequestData`. Both threads mutate shared fields:

1. **`RequestData.OutputStream`** — AsyncWorker sets it to `null` (line 442) after sending 202, then ProxyWorker or `StreamResponseAsync` reads it to determine where to stream. Between the null-set and `GetOrCreateDataStreamAsync()`, another thread could see a half-updated state.

2. **`RequestData.AsyncTriggered`** — Set by `Synchronize()` (line 715) after `_taskCompletionSource.Task` completes. ProxyWorker checks `AsyncTriggered` in `WriteResponseAsync` (line 613) and `CaptureResponseStream` (line 1470). There's a proper happens-before via `await`, so this specific flag is likely safe.

3. **`RequestData.SBStatus` and `RequestData.RequestAPIStatus`** — These property setters trigger side effects (sending ServiceBus messages and updating BackupAPI). Both are set by `RequestLifecycleManager` on the ProxyWorker thread and potentially by `AsyncWorker.StartAsync()` on its own thread. These property setters are not thread-safe and the underlying service calls (`SBRequestService.updateStatus`, `BackupAPIService.UpdateStatus`) may not be safe to call concurrently for the same request.

4. **`RequestData._requestAPIDocument`** — The `??=` (null-coalescing assignment) in property setters (lines 46, 59, 99) is not atomic. Two threads executing `_requestAPIDocument ??= RequestDataConverter.ToRequestAPIDocument(this)` simultaneously could create two separate documents, with one being silently discarded.

**Impact:** Lost status updates, duplicate ServiceBus messages, or data stream corruption for async requests.

**Proposal:**  
1. Use `Interlocked.CompareExchange` for `_requestAPIDocument ??=` pattern.
2. Document the concurrency contract: after `Synchronize()` returns, only ProxyWorker should mutate `RequestData`. Before `Synchronize()`, only `AsyncWorker.StartAsync()` should mutate output-related fields.
3. Consider making `SBStatus` and `RequestAPIStatus` setters thread-safe with a lock or queuing pattern.

---

### RC-6: `CircuitBreaker._isCurrentlyBlocked` — Non-Atomic State Transition

**File:** `Backend/CircuitBreaker.cs:27, 85-126`

```csharp
private bool _isCurrentlyBlocked = false;
```

**The Race:** `CheckFailedStatus()` is called concurrently by:
- Multiple ProxyWorkers (in `ProxyToBackEndAsync` line 893)
- Server listener (line 478)
- Health check service

The method performs a **check-then-act** pattern:
```csharp
if (isCurrentlyFailed && !_isCurrentlyBlocked)  // check
{
    _isCurrentlyBlocked = true;                   // act
    Interlocked.Increment(ref _blockedCircuitBreakersCount);
}
```
Two threads can both see `!_isCurrentlyBlocked == true` and both increment `_blockedCircuitBreakersCount`, making the global blocked count too high. This could cause `AreAllCircuitBreakersBlocked()` to return `true` prematurely, rejecting all traffic.

Similarly, the dequeue loop `while (hostFailureTimes2.TryPeek(out var t) && ...)` + `TryDequeue()` is not atomic — two threads may both dequeue the same boundary entry, removing too many failures.

**Impact:** Global circuit breaker may erroneously lock or unlock, causing traffic storms or complete traffic rejection.

**Proposal:**  Use a lock around the `CheckFailedStatus()` state-transition logic, or use `Interlocked.CompareExchange` on `_isCurrentlyBlocked` (converting it to an `int` flag).

---

## 3. Moderate Race Conditions

### RC-7: `HealthCheckService.EnterState()` — `AddOrUpdate` Return Value Misinterpretation

**File:** `Proxy/HealthCheckService.cs:100-126`

```csharp
var oldState = _workerCurrentState.AddOrUpdate(
    workerId,
    newState,
    (_, currentState) => { ... return newState; });

if (oldState.Equals(newState))
{
    EnterStateInternal(newState); // Increment counter
}
else
{
    EnterStateInternal(newState); // Increment counter
}
```

**The Race:** Per .NET documentation, `ConcurrentDictionary.AddOrUpdate` returns the **new value** for the key (either the `addValue` or the return value of the `updateValueFactory`). The variable `oldState` is misleadingly named — it always equals `newState`. The `if/else` condition on line 116 always evaluates to `true`, making both branches identical. This is correct by accident but the code structure is confusing.

The real concurrency concern: `AddOrUpdate`'s update factory calls `ExitStateInternal(currentState.Value)` which decrements a counter via `Interlocked.Decrement`. Per .NET documentation, `ConcurrentDictionary` may call the update factory **multiple times** under thread contention (the factory is not guaranteed to run exactly once). This would decrement the old state counter multiple times, making it go negative.

**Impact:** Worker state counters could become negative under contention, producing misleading health diagnostics.

**Proposal:** Use `_workerCurrentState.TryGetValue()` + `TryUpdate()` instead of `AddOrUpdate`, or move the counter management outside the lambda to avoid the retry issue.

---

### RC-8: `ConcurrentPriQueue.Enqueue` — Lock-Free Probe Fast Path Can Race with `SignalWorker`

**File:** `Queue/ConcurrentPriQueue.cs:54-76`

```csharp
if (priority == 0) {
    var t = _taskSignaler.GetNextProbeTask();
    if (t != null) {
        t.TaskCompletionSource.SetResult(item);
        return true;
    }
    var anyWorker = _taskSignaler.GetNextTask();
    if (anyWorker != null) {
        anyWorker.TaskCompletionSource.SetResult(item);
        return true;
    }
}
```

**The Race:** This fast path for probe requests runs **without any lock**. Simultaneously, `SignalWorker()` (line 107-128) is also calling `_taskSignaler.GetNextTask()`. Since `ConcurrentQueue.TryDequeue` is thread-safe, the dequeue itself is fine. However:

1. A probe enqueue at the fast path and `SignalWorker` can both dequeue a worker task from `_taskSignaler`. This is acceptable (both will serve different items).
2. But if the fast path dequeues the **only** available worker, and then the regular enqueue path adds the item to the priority queue and releases the semaphore, `SignalWorker` wakes up, finds a waiting item, but no workers available. The item sits in the queue until another worker finishes. This is a **priority inversion** — the probe was supposed to get priority but the non-probe item is now stuck.

**Impact:** Under specific timing, non-probe items can be delayed because the fast path consumed the available worker.

**Proposal:** The fast path should only consume probe-priority workers, not "any" workers. Remove the fallback to `GetNextTask()` in the probe fast path.

---

### RC-9: `ProxyWorkerCollection` — Static `_workers` and `_tasks` Lists Modified Without Synchronization

**File:** `Proxy/ProxyWorkerCollection.cs:35-36`

```csharp
private static readonly List<ProxyWorker> _workers = new();
private static readonly List<Task> _tasks = new();
```

**The Race:** These lists are populated in `ExecuteAsync()` (lines 106-128) and read in:
- `ExpelAsyncRequests()` — iterates `_workers`
- `GetAllTasks()` — returns `_tasks` reference (caller iterates)
- `RequestWorkerShutdown()` — cancels a CTS (safe)

While `ExecuteAsync()` runs to completion before shutdown methods are called (due to `BackgroundService` lifecycle), the lists are `static`, so there's a theoretical risk if multiple `ProxyWorkerCollection` instances were created (unlikely with current DI setup, but fragile).

**Impact:** Low — unlikely with current DI singleton registration, but static mutable state is a code smell.

**Proposal:** Change from `static` fields to instance fields, or ensure the lists are fully populated before any consumer accesses them (already effectively true due to `BackgroundService` start ordering).

---

### RC-10: `AsyncWorker.GetOrCreateDataStreamAsync()` — Double-Creation Race

**File:** `Proxy/AsyncWorker.cs:308-330`

```csharp
public async Task<Stream> GetOrCreateDataStreamAsync()
{
    if (_requestData.OutputStream == null)
    {
        var dataStream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(...)
        _requestData.OutputStream = new BufferedStream(dataStream);
    }
    return _requestData.OutputStream;
}
```

**The Race:** If two threads call `GetOrCreateDataStreamAsync()` concurrently (e.g., `StreamResponseAsync` and `HandleBackgroundCheckResultAsync`), both may see `OutputStream == null` and both create a new blob stream. The second assignment overwrites the first, leaving an orphaned blob stream that is never disposed and holds a write lock on the blob.

**Impact:** Orphaned blob stream, potential Azure Blob Storage contention, leaked resources.

**Proposal:** Use a `SemaphoreSlim` or `Lazy<Task<Stream>>` pattern to ensure only one blob stream is created.

---

### RC-11: `Server._isShuttingDown` — Non-Volatile Boolean

**File:** `server.cs:44, 110, 115, 285`

```csharp
private static bool _isShuttingDown = false;
```

**The Race:** `_isShuttingDown` is set to `true` by `BeginShutdown()` and `StopListening()` (called from shutdown thread). It's read in the `Run()` loop (line 285) by the listener thread. Without `volatile` or `Interlocked`, the JIT compiler may cache the value in a register on the reading thread, causing the listener to accept requests after shutdown has been signaled.

**Impact:** Requests accepted during graceful shutdown, causing 503 errors when workers have already stopped.

**Proposal:** Mark `_isShuttingDown` as `volatile`, or use `Volatile.Read`/`Volatile.Write`.

---

## 4. Low-Risk Race Conditions

### RC-12: `ProxyEvent` Copy Constructor — Shallow Copy of `ConcurrentDictionary`

**File:** `Events/ProxyEvent.cs:71-82`

```csharp
public ProxyEvent(ProxyEvent other) : base(other)
```

**The Race:** The copy constructor iterates the source dictionary. If the source is being modified concurrently (e.g., another thread is calling `SendEvent()` which sets `this["Status"]` and `this["Method"]`), the copy may see a partial/inconsistent snapshot. `ConcurrentDictionary` guarantees individual operation safety but not snapshot consistency during enumeration.

**Impact:** Minor — the copied event may have some stale or missing properties. Used in `Server.Run()` (line 607) where `ed` is being populated on the same thread.

**Proposal:** Low priority. If needed, take a snapshot using `ToArray()` before creating the copy.

---

### RC-13: `IteratorFactory` Static Cache — Double-Checked Locking Without Volatile

**File:** `Backend/Iterators/IteratorFactory.cs:152-199`

```csharp
private static volatile List<BaseHostHealth>? _cachedActiveHosts;
private static volatile List<BaseHostHealth>? _cachedSpecificPathHosts;
private static volatile List<BaseHostHealth>? _cachedCatchAllHosts;
```

These are already marked `volatile` — good. However, `InvalidateCache()` (line 250-259) nulls them inside a lock. `GetCategorizedHosts()` reads them outside the lock first (fast path). The double-checked locking pattern here is correct because `volatile` ensures the read in the fast path sees the latest value.

**Impact:** None currently — correctly implemented. Listed here for completeness.

**Proposal:** None needed.

---

### RC-14: `PriorityQueue<T>.itemCounter` — Static Across All Generic Instantiations

**File:** `Queue/PriorityQueue.cs:13`

```csharp
public static int itemCounter = 0;
```

**The Race:** `itemCounter` is `static`, meaning it is shared across all `PriorityQueue<T>` instantiations for the same type `T`. Currently there's only one `PriorityQueue<RequestData>`, so this works. But if a second queue were created, the count would be the sum of both queues' items.

**Impact:** None currently, but fragile API design.

**Proposal:** Make `itemCounter` an instance field.

---

### RC-15: `QueuedBlobStream._pendingWrites` — `List<Task>` Not Thread-Safe

**File:** `BlobStorage/QueuedBlobWriter.cs:24`

```csharp
private readonly List<Task<BlobWriteResult>> _pendingWrites = new();
```

**The Race:** `_pendingWrites.Add()` is called in `FlushAsync()` and `_pendingWrites.Clear()` + iteration happen in `WaitForPendingWritesAsync()`. If `FlushAsync()` and `WaitForPendingWritesAsync()` are called concurrently, `List.Add` during `Task.WhenAll` enumeration can cause `InvalidOperationException` (collection modified during enumeration).

In the current flow, `FlushAsync()` should be done before `WaitForPendingWritesAsync()` is called, but there's no enforcement of this ordering.

**Impact:** Potential `InvalidOperationException` if stream is flushed while waiting for pending writes.

**Proposal:** Use `ConcurrentBag<Task>` or coordinate access with a lock.

---

## 5. Design-Level Concerns

### D-1: `RequestData` Has No Concurrency Contract

`RequestData` is a large mutable object shared between threads (Server → Queue → ProxyWorker → AsyncWorker). There's no documentation or enforcement of which thread "owns" the object at which lifecycle stage. Properties like `OutputStream`, `SBStatus`, `RequestAPIStatus`, `SkipDispose`, `Requeued`, and `asyncWorker` are freely read and written from multiple contexts.

**Recommendation:** Define clear ownership epochs:
1. **Server thread** owns during creation and enqueueing.
2. **ProxyWorker thread** owns after dequeueing, until Synchronize.
3. **AsyncWorker thread** owns only the 202-response path.
4. After `Synchronize()`, ownership returns to ProxyWorker exclusively.

### D-2: `ProxyWorker` Instance Fields Used Like Thread-Local State

`_asyncExpelSource` and `_isEvictingAsyncRequest` are instance fields on `ProxyWorker`, but each worker runs on its own task, so they act like thread-local state. However, `ExpelAsyncRequest()` is called from `ProxyWorkerCollection.ExpelAsyncRequests()` running on the shutdown thread. This cross-thread access to `_asyncExpelSource` and `_isEvictingAsyncRequest` has no memory barrier.

**Recommendation:** Mark `_isEvictingAsyncRequest` as `volatile`.

### D-3: `SetupAsyncWorkerAndTimeout` — CTS Disposal Race

In `ProxyWorker.SetupAsyncWorkerAndTimeout()` (line 1687-1717), `_asyncExpelSource?.Dispose()` is called before creating a new CTS. But `ExpelAsyncRequest()` (line 693) reads and cancels `_asyncExpelSource` from the shutdown thread. If shutdown calls `ExpelAsyncRequest()` just after `Dispose()` but before the new CTS is assigned, `_asyncExpelSource.Cancel()` throws `ObjectDisposedException` — which is caught, but the request won't be expelled.

**Recommendation:** Use `Interlocked.Exchange` to swap the CTS atomically.

---

## 6. Summary Matrix

| ID | Severity | Component | Issue | Impact |
|----|----------|-----------|-------|--------|
| RC-1 | **Critical** | ProxyWorker | Static ProxyEvent reuse across workers | Corrupted telemetry |
| RC-2 | **Critical** | PriorityQueue | Count check outside lock; static refItem | Queue overflow, potential corruption |
| RC-3 | **Critical** | ConcurrentSignal | Probe worker non-atomic flag | Lost workers, double-completion |
| RC-4 | **Critical** | Backends | `_activeHosts` no memory barrier | Stale host lists |
| RC-5 | **Critical** | RequestData + AsyncWorker | Concurrent mutation of shared properties | Lost updates, stream corruption |
| RC-6 | **Critical** | CircuitBreaker | Non-atomic state transition | Erroneous global CB state |
| RC-7 | Moderate | HealthCheckService | `AddOrUpdate` retry calling exit multiple times | Negative state counters |
| RC-8 | Moderate | ConcurrentPriQueue | Probe fast path consuming wrong worker | Priority inversion |
| RC-9 | Moderate | ProxyWorkerCollection | Static mutable lists | Fragile code |
| RC-10 | Moderate | AsyncWorker | Double-creation of blob stream | Resource leak |
| RC-11 | Moderate | Server | `_isShuttingDown` not volatile | Late shutdown detection |
| RC-12 | Low | ProxyEvent | Copy constructor snapshot | Inconsistent telemetry copy |
| RC-13 | Low | IteratorFactory | Cache pattern | Correctly implemented |
| RC-14 | Low | PriorityQueue | Static itemCounter | Fragile for multi-instance |
| RC-15 | Low | QueuedBlobStream | `_pendingWrites` not thread-safe | Potential exception |
| D-1 | Design | RequestData | No concurrency ownership contract | Error-prone development |
| D-2 | Design | ProxyWorker | Cross-thread field access without barriers | Stale reads on shutdown |
| D-3 | Design | ProxyWorker | CTS disposal/swap race | Missed expulsion |

---

## Priority Fix Order

1. **RC-1** (Static ProxyEvent reuse) — Highest ROI, easy fix, prevents data corruption.
2. **RC-3** (ConcurrentSignal probe worker) — Can cause stuck workers.
3. **RC-6** (CircuitBreaker state transition) — Can cause complete traffic rejection.
4. **RC-5** (`RequestData` concurrent mutation) — Critical for async correctness.
5. **RC-4** (`_activeHosts` volatile) — One-line fix, prevents stale routing.
6. **RC-11** (`_isShuttingDown` volatile) — One-line fix.
7. **RC-2** (PriorityQueue count check) — Move check inside lock.
8. **RC-7** (HealthCheckService `AddOrUpdate`) — Fix lambda retry issue.
9. **RC-10** (AsyncWorker double-creation) — Add lazy initialization guard.
10. Remaining items as time permits.
