/// <summary>
/// Defines the type of request being processed by the proxy worker.
/// This enum clarifies the four distinct request handling modes.
/// </summary>

/*
 * REQUEST EXECUTION MODES:
 * 
 * ┌──────────────────────────────────────────────────────────────────────────────┐
 * │                           STATE TRANSITIONS                                  │
 * │                                                                              │
 * │   ┌──────────┐    runAsync=true OR     ┌──────────┐                          │
 * │   │   SYNC   │ ──────timeout near────► │  ASYNC   │                          │
 * │   │          │                         │          │                          │
 * │   │ runAsync │                         │ runAsync │                          │
 * │   │ = false  │                         │ = true   │                          │
 * │   └────┬─────┘                         └────┬─────┘                          │
 * │        │                                    │                                │
 * │        ▼                                    │  batch ID detected             │
 * │   ┌─────────┐                               ▼                                │
 * │   │ Client  │                         ┌───────────────┐                      │
 * │   │ HTTP    │                         │ ASYNC         │                      │
 * │   │ Response│                         │ BACKGROUND    │                      │
 * │   └─────────┘                         │ IsBackground  │                      │
 * │                                       │ = true        │                      │
 * │                                       └───────┬───────┘                      │
 * │                                               │                              │
 * │                                               │  Azure Function schedules    │
 * │                                               ▼                              │
 * │                                       ┌────────────────────┐                 │
 * │                                       │ ASYNC BACKGROUND   │                 │
 * │                                       │ CHECK              │◄────┐           │
 * │                                       │ IsBackgroundCheck  │     │ pending   │
 * │                                       │ = true             │─────┘           │
 * │                                       └────────┬───────────┘                 │
 * │                                                │ complete                    │
 * │                                                ▼                             │
 * │                                       ┌─────────────────┐                    │
 * │                                       │  Blob Storage   │                    │
 * │                                       │  (final result) │                    │
 * │                                       └─────────────────┘                    │
 * └──────────────────────────────────────────────────────────────────────────────┘
 * 
 * KEY FLAGS:
 *   runAsync          → Response goes to blob storage (not client)
 *   AsyncTriggered    → AsyncWorker.StartAsync() completed setup  
 *   IsBackground      → Backend returned batch ID for later completion
 *   IsBackgroundCheck → Polling for batch completion status
 * 
 * RESPONSE ROUTING:
 *   Sync            → request.Context.Response (HTTP connection)
 *   Async           → asyncWorker → blob storage
 *   AsyncBackground → batch ID → blob + RequestAPI status update
 *   AsyncBgCheck    → memory buffer → blob if complete, else re-queue
 */ 

public enum RequestType
{
    /// <summary>
    /// Synchronous request - Response streamed directly to client HTTP connection.
    /// No blob storage involved. Completes within timeout threshold.
    /// </summary>
    Sync = 0,

    /// <summary>
    /// Asynchronous request - Processing time exceeded AsyncTriggerTimeout.
    /// Response written to blob storage. Client receives 202 Accepted with tracking URL.
    /// No background batch processing involved.
    /// </summary>
    Async = 1,

    /// <summary>
    /// Background request (initial submission) - Request submitted to backend batch API (e.g., OpenAI Batch).
    /// Backend returns a batch ID for later polling. Response written to blob storage.
    /// Client receives 202 Accepted with tracking URL.
    /// </summary>
    AsyncBackground = 2,

    /// <summary>
    /// Background check request - Periodic polling to check status of previously submitted background request.
    /// URL is modified to append batch ID. If completed, final response is retrieved and stored.
    /// Triggered by backgroundReqChecker Azure Function.
    /// </summary>
    AsyncBackgroundCheck = 3
}
