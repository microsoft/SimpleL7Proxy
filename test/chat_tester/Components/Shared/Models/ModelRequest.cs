namespace chat_tester.Components.Shared;

/// <summary>
/// An outgoing model request produced by a model component: the endpoint path to send it to,
/// the serialized request body, and the content type to use.
/// </summary>
/// <param name="EndpointPath">Endpoint path the request is sent to.</param>
/// <param name="Body">Serialized request body.</param>
/// <param name="ContentType">Content-Type header value for the request.</param>
public sealed record ModelRequest(string EndpointPath, string Body, string ContentType);
