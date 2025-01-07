namespace TestClient
{
    public record ApplicationSettings(AzureOpenAI AzureOpenAI);

    public record AzureOpenAI(
        string Key,
        string DeploymentName,
        string Endpoint
    );
}
