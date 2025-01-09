using System.Net.Http.Json;

namespace TestClient.Services
{
    public class EchoAPIService(HttpClient httpClient)
    {
        public async Task<string> ResourceChange(Resource resource, string subscriptionKey, string endpoint)
        {
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

            var url = $"{endpoint}echo/resource";
            HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, resource);

            return await response.Content.ReadAsStringAsync();
        }
    }
}
