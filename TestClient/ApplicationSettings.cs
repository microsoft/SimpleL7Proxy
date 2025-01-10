namespace TestClient
{
    public record ApplicationSettings(
        AzureOpenAI AzureOpenAI,
        ClientSettings ClientSettings
    );

    public record AzureOpenAI(
        string Key,
        string DeploymentName,
        string Endpoint
    );

    public record ClientSettings(
        string Endpoint,
        string SubscriptionKey
    );

    public record Resource(string VehicleType, string MaxSpeed, string AvgSpeed, string SpeedUnit);
}
