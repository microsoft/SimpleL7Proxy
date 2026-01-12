namespace SimpleL7Proxy.Proxy;

/// <summary>
/// Represents the different states a worker can be in during request processing
/// </summary>
public enum WorkerState
{
    /// <summary>Waiting to dequeue a request from the queue</summary>
    Dequeuing = 0,
    
    /// <summary>Pre-processing the request (validation, setup)</summary>
    PreProcessing = 1,
    
    /// <summary>Proxying the request to backend</summary>
    Proxying = 2,
    
    /// <summary>Sending request to backend</summary>
    Sending = 3,
    
    /// <summary>Receiving response from backend</summary>
    Receiving = 4,
    
    /// <summary>Writing response to client</summary>
    Writing = 5,
    
    /// <summary>Reporting/logging request results</summary>
    Reporting = 6,
    
    /// <summary>Cleaning up request resources</summary>
    Cleanup = 7
}