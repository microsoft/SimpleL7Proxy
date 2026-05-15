using Shared.RequestAPI.Models;

namespace SimpleL7Proxy.Async.Feeder
{
    public interface IRequestProcessor
    {
        Task HydrateRequestAsync(RequestData data);
    }
}