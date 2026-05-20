using Shared.RequestAPI.Models;

namespace SimpleL7Proxy.Async.Jobs
{
    public interface IRequestProcessor
    {
        Task HydrateRequestAsync(RequestData data);
    }
}