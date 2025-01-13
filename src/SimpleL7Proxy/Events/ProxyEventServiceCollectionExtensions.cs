using Microsoft.Extensions.DependencyInjection;

namespace SimpleL7Proxy.Events;

public static class ProxyEventServiceCollectionExtensions
{
  // TODO: Pass in service configuration
  public static IServiceCollection AddProxyEventClient(
    this IServiceCollection services,
    string? eventHubConnectionString,
    string? eventHubName,
    string? aiConnectionString)
  {

    if (!(string.IsNullOrEmpty(eventHubConnectionString) || string.IsNullOrEmpty(eventHubName)))
    {
      services.AddSingleton(svc => new EventHubClient(eventHubConnectionString, eventHubName));
    }

    if (!string.IsNullOrEmpty(aiConnectionString))
    {
      services.AddSingleton<AppInsightsEventClient>();
    }

    services.AddSingleton<IEventClient, CompositeEventClient>(svc =>
    {
      var clients = new List<IEventClient>();
      var eventHubClient = svc.GetService<EventHubClient>();
      var appInsightsClient = svc.GetService<AppInsightsEventClient>();
      if (eventHubClient != null)
      {
        clients.Add(eventHubClient);
      }
      if (appInsightsClient != null)
      {
        clients.Add(appInsightsClient);
      }
      return new CompositeEventClient(clients);
    });

    return services;
  }
}
