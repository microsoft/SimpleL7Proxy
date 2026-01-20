# Object Lifecycle Analysis for Streaming Pipeline

This document describes the ownership, lifecycle, and disposal responsibilities for key objects in the proxy streaming pipeline.

---

## Object Ownership & Disposal Responsibilities

### RequestData (`IAsyncDisposable`)

| Aspect | Details |
|--------|---------|
| **Created** | HttpListener callback or AsyncFeeder |
| **Owner** | `TaskRunnerAsync()` via `await using` |
| **Disposed** | End of `await using` block OR explicit `Dispose()` in finally |

**Contains:**
| Member | Disposal |
|--------|----------|
| `Body` (Stream) | `DisposeAsyncCore()` |
| `Context.Request.InputStream` | `DisposeAsyncCore()` |
| `Context.Response.OutputStream` | `Dispose(bool)` |
| `OutputStream` | Disposed if `!= Context.Response.OutputStream` |
| `asyncWorker` | `DisposeAsyncCore()` |

---

### ProxyData (`IDisposable`)

| Aspect | Details |
|--------|---------|
| **Created** | `ProxyToBackEndAsync()` on successful backend response |
| **Owner** | `ProxyToBackEndAsync()` caller (`TaskRunnerAsync`) |
| **Disposed** | `pr?.Dispose()` after `WriteResponseAsync()` in `TaskRunnerAsync` |

**Contains:**
| Member | Disposal |
|--------|----------|
| `Body` (byte[]) | Set to `null` in `Dispose()` |
| `BodyResponseMessage` | `HttpResponseMessage`, disposed in `Dispose()` |
| `Headers` | Cleared in `Dispose()` |
| `ContentHeaders` | Cleared in `Dispose()` |

---

### HttpResponseMessage (`proxyResponse`)

| Aspect | Details |
|--------|---------|
| **Created** | `HttpClient.SendAsync()` in `ProxyToBackEndAsync` |
| **Stored** | `pr.BodyResponseMessage` (ownership transferred to ProxyData) |
| **Used By** | `CaptureResponseStream()`, `StreamResponseAsync()` |
| **Disposed** | Via `ProxyData.Dispose()` → `BodyResponseMessage?.Dispose()` |

> ⚠️ **WARNING:** Do NOT add `using` on `proxyResponse` - it is disposed via `ProxyData`

---

### IStreamProcessor (`IDisposable`)

| Aspect | Details |
|--------|---------|
| **Created** | `StreamProcessorFactory.GetStreamProcessor()` in `StreamResponseAsync` |
| **Owner** | `StreamResponseAsync()` method |
| **Disposed** | finally block → `(processor as IDisposable)?.Dispose()` |

---

### MemoryStream (`memoryBuffer`) — Background checks only

| Aspect | Details |
|--------|---------|
| **Created** | `StreamResponseAsync()` when `IsBackgroundCheck=true` |
| **Owner** | `StreamResponseAsync()` method |
| **Disposed** | finally block → `memoryBuffer?.Dispose()` |

---

### CancellationTokenSource (`requestCts`)

| Aspect | Details |
|--------|---------|
| **Created** | `SetupAsyncWorkerAndTimeout()` |
| **Owner** | `ProxyToBackEndAsync()` via `using` statement |
| **Disposed** | End of `using (requestCts)` block |

---

### AsyncWorker

| Aspect | Details |
|--------|---------|
| **Created** | `SetupAsyncWorkerAndTimeout()` via `_asyncWorkerFactory` |
| **Stored** | `request.asyncWorker` (ownership with RequestData) |
| **Started** | `_ = request.asyncWorker.StartAsync()` (fire-and-forget) |
| **Disposed** | `RequestData.DisposeAsyncCore()` OR `AbortAsync()` on eviction |

---

## Stream Flow Diagram

```
┌───────────┐    ┌──────────────────┐    ┌─────────────────┐    ┌──────────────────┐
│  Backend  │───►│ proxyResponse    │───►│ IStreamProcessor│───►│  Destination     │
│  Server   │    │ .Content         │    │ .CopyToAsync()  │    │  Stream          │
└───────────┘    └──────────────────┘    └─────────────────┘    └──────────────────┘
                        │                                              │
                        │                        ┌─────────────────────┼─────────────────────┐
                        │                        │                     │                     │
                        ▼                  [Sync mode]          [Async mode]        [BgCheck mode]
                 Stored in ProxyData       request.OutputStream  asyncWorker.      MemoryStream
                 .BodyResponseMessage      (client HTTP)         GetOrCreateData   (temp buffer)
                                                                 StreamAsync()     then → blob
```

---

## Critical Disposal Notes

1. **`proxyResponse` is NOT disposed directly** — ownership transfers to `ProxyData`
2. **`ProxyData.Dispose()` MUST be called** to release `HttpResponseMessage`
3. **`RequestData` uses `await using` pattern** for proper async disposal
4. **`SkipDispose` flag** prevents premature disposal during requeue scenarios
5. **`AsyncWorker` disposal is async** — must use `DisposeAsyncCore()`