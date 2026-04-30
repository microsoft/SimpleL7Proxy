namespace SimpleL7Proxy.Async;

/// <summary>
/// Identifies a canned message template loaded from the "templates" blob container.
/// </summary>
public enum AsyncResponseTypeEnum
{
    Welcome,
    NotReady,
    NotAuthorized,
}