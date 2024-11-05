using System;
using Azure.Identity;
using Azure.Core;

class Program
{
    static void Main(string[] args)
    {
        string tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "";
        string clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? "";
        string clientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? "";
        string audience = Environment.GetEnvironmentVariable("AZURE_AUDIENCE") ?? "https://management.azure.com/.default";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            Console.WriteLine("Please set the following environment variables:");
            Console.WriteLine("  AZURE_TENANT_ID: Your Azure tenant ID");
            Console.WriteLine("  AZURE_CLIENT_ID: Your Azure client ID");
            Console.WriteLine("  AZURE_CLIENT_SECRET: Your Azure client secret");
            Console.WriteLine("Optional:");
            Console.WriteLine("  AZURE_AUDIENCE: The audience for the token (default: 'https://management.azure.com/.default')");
            Environment.Exit(1);
        }

        try
        {
            string token = GetToken(tenantId, clientId, clientSecret, audience);
            Console.WriteLine($"Access Token: {token}");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    static string GetToken(string tenantId, string clientId, string clientSecret, string audience)
    {
        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var tokenRequestContext = new TokenRequestContext(new[] { audience });
        var token = credential.GetToken(tokenRequestContext);
        return token.Token;
    }
}