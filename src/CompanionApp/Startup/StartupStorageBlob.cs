using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;
using System.Text.Json.Nodes;
using CompanionApp.Components.Shared;
using SharedParser.Profiles;

namespace CompanionApp.Startup;

internal static class StartupStorageBlob
{
    internal static async Task CheckProfileStorageAsync(WebApplication app)
    {
        var checkpoints = new StringBuilder("CheckProfileStorageAsync: entered");
        var stage = "entered";
        var profileStorageAccountName = app.Configuration["CompanionApp:Profiles:StorageAccountName"];
        var profileBlobContainerName = app.Configuration["CompanionApp:Profiles:BlobContainerName"] ?? "profiles";
        if (!string.IsNullOrWhiteSpace(profileStorageAccountName))
        {
            checkpoints.Append($"; account={profileStorageAccountName}, container={profileBlobContainerName}");
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
                checkpoints.Append("; container properties read");
                stage = "container properties read";
                app.Logger.LogInformation(
                    "Profile storage OK: read container {Container}.",
                    profileBlobContainerName);
            }
            catch (Exception exception)
            {
                checkpoints.Append($"; storage check failed ({exception.GetType().Name})");
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
            checkpoints.Append("; no account, check skipped");
            stage = "skipped, no account configured";
            app.Logger.LogInformation("Profile storage check skipped: no storage account configured.");
        }

        if (Program.DEBUGLEVEL >= 2)
        {
            Console.WriteLine(checkpoints.ToString());
        }
        else if (Program.DEBUGLEVEL == 1)
        {
            Console.WriteLine($"CheckProfileStorageAsync: {stage}");
        }
    }

    /// <summary>Creates an empty JSON blob for each defaulted profile URL when the blob does not already exist.</summary>
    internal static async Task CreateDefaultProfileBlobsAsync(
        WebApplication app,
        IReadOnlyList<AppConfigurationScaffoldSetting> drafts,
        IReadOnlyList<string> defaultedKeys)
    {
        var checkpoints = new StringBuilder("CreateDefaultProfileBlobsAsync: entered");
        var stage = "entered";
        if (defaultedKeys.Count == 0)
        {
            checkpoints.Append("; no keys defaulted, nothing to do");
            stage = "no keys defaulted";
            if (Program.DEBUGLEVEL >= 2)
            {
                Console.WriteLine(checkpoints.ToString());
            }
            else if (Program.DEBUGLEVEL == 1)
            {
                Console.WriteLine($"CreateDefaultProfileBlobsAsync: {stage}");
            }
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var credential = app.Services.GetRequiredService<DefaultAzureCredential>();

            // Sample user profiles come from CompanionApp:UserNames in chat-models.json, entries formatted as "userId, priority".
            var profileDocument = new ProfileConfigurationDocument();
            var userNames = app.Configuration.GetSection("CompanionApp:UserNames").Get<string[]>() ?? [];
            checkpoints.Append($"; UserNames entries={userNames.Length}");
            foreach (var userName in userNames)
            {
                var parts = userName.Split(',', 2, StringSplitOptions.TrimEntries);
                var profile = new ProfileConfigurationEntry { Id = parts[0] };
                profile.Fields.Add(new ProfileConfigurationField { Name = "S7PPriorityKey", Value = parts.ElementAtOrDefault(1) ?? "" });
                profileDocument.Profiles.Add(profile);
            }
            var userProfilesJson = profileDocument.Export();
            checkpoints.Append($"; user profiles built={profileDocument.Profiles.Count}, exported");
            stage = "user profiles built";

            var profileKeys = defaultedKeys.Where(key => key.StartsWith("Warm:Profiles:", StringComparison.Ordinal)).ToList();
            checkpoints.Append($"; defaulted keys={defaultedKeys.Count} [{string.Join(", ", defaultedKeys)}], profile keys={profileKeys.Count}");

            foreach (var key in profileKeys)
            {
                // Defaulted URLs are azblob://{account}/{container}/{blob}.
                var draftValue = drafts.Single(setting => setting.Key == key).DraftValue;
                checkpoints.Append($"; {key} draft={draftValue}");
                var url = new Uri(draftValue);
                var pathSegments = url.AbsolutePath.TrimStart('/').Split('/', 2);
                var containerName = pathSegments[0];
                var blobName = pathSegments[1];
                checkpoints.Append($"; {key} -> {containerName}/{blobName}");

                var blob = new BlobServiceClient(
                    new Uri($"https://{url.Host}.blob.core.windows.net"),
                    credential)
                    .GetBlobContainerClient(containerName)
                    .GetBlobClient(blobName);

                if ((await blob.ExistsAsync(timeout.Token)).Value)
                {
                    if (key != "Warm:Profiles:User:UserConfigUrl")
                    {
                        app.Logger.LogDebug(
                            "Profile blob {Container}/{Blob} already exists; default not written.",
                            containerName,
                            blobName);
                        checkpoints.Append($"; {containerName}/{blobName} exists");
                        stage = $"{key} ready";
                        continue;
                    }

                    var existing = await blob.DownloadContentAsync(timeout.Token);
                    var existingJson = existing.Value.Content.ToString();
                    var sharedProfiles = new UserProfiles();
                    if (!sharedProfiles.TryParse(existingJson, "userId", app.Logger))
                    {
                        throw new InvalidOperationException($"Existing {containerName}/{blobName} could not be parsed: {sharedProfiles.Error}");
                    }
                    var added = profileDocument.Profiles.Where(profile => !sharedProfiles.Profiles.ContainsKey(profile.Id)).ToList();
                    checkpoints.Append($"; {containerName}/{blobName} exists, existing profiles={sharedProfiles.Profiles.Count}, appending={added.Count}");
                    if (added.Count == 0)
                    {
                        stage = $"{key} ready";
                        continue;
                    }

                    var merged = JsonNode.Parse(existingJson) as JsonArray
                        ?? throw new InvalidOperationException($"Existing {containerName}/{blobName} is not a JSON array.");
                    // Keep the shared rule-library entry (no userId) as the final element.
                    var insertAt = merged.Count > 0 && merged[merged.Count - 1]?["userId"] is null ? merged.Count - 1 : merged.Count;
                    foreach (var profile in added)
                    {
                        var fields = profile.Fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value));
                        merged.Insert(insertAt++, JsonNode.Parse(sharedProfiles.ToProfileString("userId", profile.Id, fields, profile.RuleNames)));
                    }
                    await blob.UploadAsync(
                        BinaryData.FromString(merged.ToJsonString()),
                        new BlobUploadOptions
                        {
                            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                            Conditions = new BlobRequestConditions { IfMatch = existing.Value.Details.ETag }
                        },
                        timeout.Token);
                    checkpoints.Append($"; {containerName}/{blobName} appended {added.Count} profiles");
                    stage = $"{key} ready";
                    app.Logger.LogInformation(
                        "Appended {Count} default profiles to {Container}/{Blob}.",
                        added.Count,
                        containerName,
                        blobName);
                    continue;
                }

                checkpoints.Append($"; {containerName}/{blobName} missing, uploading");
                await blob.UploadAsync(
                    BinaryData.FromString(key == "Warm:Profiles:User:UserConfigUrl" ? userProfilesJson : "[]"),
                    new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
                    },
                    timeout.Token);
                checkpoints.Append($"; {containerName}/{blobName} created");
                stage = $"{key} ready";
                app.Logger.LogInformation(
                    "Created default profile blob {Container}/{Blob} for {Key}.",
                    containerName,
                    blobName,
                    key);
            }
            checkpoints.Append("; loop finished");
            stage = $"processed {profileKeys.Count} profile URL(s)";
        }
        catch (Exception exception)
        {
            checkpoints.Append($"; failed ({exception.GetType().Name}: {exception.Message})");
            app.Logger.LogError(
                exception,
                "Default profile blob creation failed ({ExceptionType}: {ExceptionMessage}). Check storage permissions. Startup will continue.",
                exception.GetType().Name,
                exception.Message);
            app.Logger.LogDebug(exception, "Default profile blob failure details.");
        }

        if (Program.DEBUGLEVEL >= 2)
        {
            Console.WriteLine(checkpoints.ToString());
        }
        else if (Program.DEBUGLEVEL == 1)
        {
            Console.WriteLine($"CreateDefaultProfileBlobsAsync: {stage}");
        }
    }
}
