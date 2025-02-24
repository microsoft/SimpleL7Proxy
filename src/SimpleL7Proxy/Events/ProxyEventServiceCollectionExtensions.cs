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

    if (!string.IsNullOrEmpty(aiConnectionString))
    {
      try
      {  
        services.AddSingleton<AppInsightsEventClient>();
      }
      catch (Exception ex)
      {
        Console.WriteLine("Failed to create AppInsightsEventClient: " + ex.Message);
      }
    }
    
    if (!(string.IsNullOrEmpty(eventHubConnectionString) || string.IsNullOrEmpty(eventHubName)))
    {
      try
      {
        services.AddSingleton(svc => new EventHubClient(eventHubConnectionString, eventHubName));
      }
      catch (Exception ex)
      {
        Console.WriteLine("Failed to create EventHubClient: " + ex.Message);
      }
    }


    services.AddSingleton<IEventClient, CompositeEventClient>(svc =>
    {
      List<IEventClient> clients = [];
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
      return new(clients);
    });

    return services;
  }
}
