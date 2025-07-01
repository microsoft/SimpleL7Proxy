# Proxy Response Codes and Headers

## Response Codes

| Code | Description                                                                                  |
|----- |----------------------------------------------------------------------------------------------|
| 200  | Success                                                                                      |
| 400  | Bad Request (Issue with the HTTP request format)                                             |
| 408  | Request Timed Out (The request to the backend host timed out)                                |
| 412  | Request Expired (S7PTTL indicates the request is too old to process)                         |
| 429  | The queue is full or circuit breaker has tripped and the service not accepting requests currently   |
| 500  | Internal Server Error (Check Application Insights for more details)                          |
| 502  | Bad Gateway (Could not complete the request, possibly from overloaded backend hosts)         |

## Headers Used

| Header                 | Description                                                                                                       |
|------------------------|-------------------------------------------------------------------------------------------------------------------|
| **S7PDEBUG**           | Set to `true` to enable tracing at the request level.                                                             |
| **S7PPriorityKey**     | If this header matches a defined key in *PriorityKeys*, the request uses the associated priority from *PriorityValues*. |
| **S7PREQUEUE**         | If a remote host returns `429` and sets this header to `true`, the request will be requeued using its original enqueue time. |
| **S7PTTL**             | Time-to-live for a message. Once expired, the proxy returns `410` for that request.                                |
| **x-Request-Queue-Duration**  | Shows how long the message spent in the queue before being processed.                                       |
| **x-Request-Process-Duration**| Indicates the processing duration of the request.                                                           |
| **x-Request-Worker**          | Identifies which worker ID handled the request.                                                             |
