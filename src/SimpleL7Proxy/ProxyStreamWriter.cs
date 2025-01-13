using CommunityToolkit.Diagnostics;
using SimpleL7Proxy.Proxy;

namespace SimpleL7Proxy;

public class ProxyStreamWriter
{
    private void OverrideResponseHeaders(
        IHttpListenerResponse response,
        HttpResponseMessage responseMessage)
    {
        Guard.IsNotNull(response, nameof(response));
        Guard.IsNotNull(responseMessage, nameof(responseMessage));
        Guard.IsNotNull(responseMessage.Content, nameof(responseMessage.Content));
        Guard.IsNotNull(responseMessage.Content.Headers, nameof(responseMessage.Content.Headers));

        // Set the response status code  
        response.StatusCode = (int)responseMessage.StatusCode;
        //TODO: Why flag as false?
        response.KeepAlive = false;

        var contentHeaders = responseMessage.Content.Headers;
        foreach (var header in contentHeaders)
        {
            response.Headers[header.Key] = string.Join(", ", header.Value);
            if (header.Key.ToLower().Equals("content-length"))
            {
                response.ContentLength64 = contentHeaders.ContentLength ?? 0;
            }
        }
    }

    public async Task WriteResponseDataAsync(
        IHttpListenerResponse response,
        ProxyData proxyData,
        CancellationToken token)
    {
        var responseMessage = proxyData.ResponseMessage;
        OverrideResponseHeaders(response, responseMessage);

        var outputStream = response.OutputStream;
        var content = responseMessage.Content;
        if (content != null)
        {
            await using var responseStream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await responseStream.CopyToAsync(outputStream, token).ConfigureAwait(false);
            await outputStream.FlushAsync(token).ConfigureAwait(false);
        }
    }
}
