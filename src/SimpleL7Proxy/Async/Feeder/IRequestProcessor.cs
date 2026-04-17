using Shared.RequestAPI.Models;

namespace SimpleL7Proxy.Feeder
{
    public interface IRequestProcessor
    {
        Task HydrateRequestAsync(RequestData data);
    }
}