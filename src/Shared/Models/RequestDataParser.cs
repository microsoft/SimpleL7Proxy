using System.Text.Json;

namespace Shared.RequestAPI.Models;

public static class RequestDataParser
{
    public static IRequestData? ParseRequestData(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return null;

        try
        {
            // Try RequestAPIDocument first (more specific)
            if (IsRequestAPIDocument(jsonString))
            {
                return JsonSerializer.Deserialize<RequestAPIDocument>(jsonString);
            }
            
            // Fall back to RequestMessage
            return JsonSerializer.Deserialize<RequestMessage>(jsonString);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static T? ParseAs<T>(string jsonString) where T : IRequestData
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(jsonString);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool IsRequestAPIDocument(string jsonString)
    {
        // Check for properties unique to RequestAPIDocument
        return jsonString.Contains("\"createdAt\"") || 
               jsonString.Contains("\"priority1\"") || 
               jsonString.Contains("\"status\"");
    }
}