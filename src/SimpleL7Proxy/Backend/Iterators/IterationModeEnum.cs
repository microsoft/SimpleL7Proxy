namespace SimpleL7Proxy.Backend.Iterators;

public enum IterationModeEnum
{
    SinglePass,      // Try each host once
    MultiPass        // Retry the entire host list repeatedly until aborted
}
