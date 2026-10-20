using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CompanionApp.Startup;

internal static class StartupStorageBlob
{
    internal static async Task CheckProfileStorageAsync(WebApplication app)
    {
        var profileStorageAccountName = app.Configuration["CompanionApp:Profiles:StorageAccountName"];
        var profileBlobContainerName = app.Configuration["CompanionApp:Profiles:BlobContainerName"] ?? "profiles";
        if (!string.IsNullOrWhiteSpace(profileStorageAccountName))
        {
            app.Logger.LogDebug(
                "Profile storage: checking read and write access to account {Account}, container {Container}.",
                profileStorageAccountName,
                profileBlobContainerName);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var container = new BlobServiceClient(
                    new Uri($"https://{profileStorageAccountName}.blob.core.windows.net"),
                    app.Services.GetRequiredService<DefaultAzureCredential>())
                    .GetBlobContainerClient(profileBlobContainerName);
                await container.GetPropertiesAsync(cancellationToken: timeout.Token);
                // Temporary startup probe: remove after deployed profile storage access is verified.
                await container.GetBlobClient("hello.json").UploadAsync(
                    BinaryData.FromString("{\"message\":\"hello\"}"),
                    new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
                    },
                    timeout.Token);
                app.Logger.LogInformation(
                    "Profile storage OK: wrote {Container}/hello.json.",
                    profileBlobContainerName);
            }
            catch (Exception exception)
            {
                app.Logger.LogError(
                    exception,
                    "Profile storage check failed: {Account}/{Container}. Check connectivity and storage permissions. Startup will continue.",
                    profileStorageAccountName,
                    profileBlobContainerName);
                app.Logger.LogDebug(exception, "Profile storage failure details.");
            }
        }
        else
        {
            app.Logger.LogInformation("Profile storage check skipped: no storage account configured.");
        }
    }
}
