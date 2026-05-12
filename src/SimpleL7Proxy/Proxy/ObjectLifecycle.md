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

### IBlobWriterFactory / GenericBlobFactory (Singleton)

| Aspect | Details |
|--------|---------|
| **Created** | DI container at startup; concrete type resolved via `AsyncClassNames` map (`BlobWriterFactory` for Azure, `S3BlobWriterFactory` stub for S3) |
| **Owner** | DI container (singleton, app lifetime) |
| **Role** | Produces `IBlobWriter` (raw, per-call via `CreateBlobWriter()`) and `IQueuedBlobWriter` (singleton via `CreateQueuedBlobWriter()`) |
| **Disposed** | Not disposed; lives for process lifetime |

> Backend selection is config-driven — swap `IBlobWriterFactory:BlobWriterFactory` → `IBlobWriterFactory:S3BlobWriterFactory` in `AsyncClassNames` to change blob backend without code changes.

---

### IBlobWriter (raw) — `AzureBlobWriter` / `S3BlobWriter` / `NullBlobWriter`

| Aspect | Details |
|--------|---------|
| **Created** | `IBlobWriterFactory.CreateBlobWriter()` — fresh instance per call |
| **Used By** | `AsyncStreamingStore` (multi-GB streams, bypasses queue); wrapped by `QueuedBlobWriter` for small writes |
| **Disposed** | By the holder (`AsyncStreamingStore` / `QueuedBlobWriter`) — implements `IDisposable` |

> Raw writer is for streaming paths where the caller manages backpressure. Small one-shot writes should go through `IQueuedBlobWriter`.

---

### IQueuedBlobWriter (`QueuedBlobWriter`) — Singleton decorator

| Aspect | Details |
|--------|---------|
| **Created** | DI singleton: `sp.GetRequiredService<IBlobWriterFactory>().CreateQueuedBlobWriter()` |
| **Composes** | Inner raw `IBlobWriter` (from factory) + `BlobWorkerPump` (shared queue) |
| **Used By** | `AsyncFileStore` (small at-once blobs: headers, status, control payloads) |
| **Disposed** | Not disposed; singleton for process lifetime. Inner writer disposed transitively if needed. |

---

### BlobWorkerPump (`Lazy<BlobWorkerPump>`)

| Aspect | Details |
|--------|---------|
| **Created** | Lazily within `GenericBlobFactory` — `Lazy<BlobWorkerPump>` breaks the DI cycle (pump depends on factory, factory exposes pump through `CreateQueuedBlobWriter`) |
| **Role** | Background worker loop that drains the shared queue and forwards each write to the raw `IBlobWriter` |
| **Disposed** | Process lifetime; drained on shutdown |

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