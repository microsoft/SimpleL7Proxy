namespace chat_tester.Components.Shared;

public sealed class ConversationSettings
{
    private ConversationStorageSettings _settings = new();

    public ConversationStorageSettings Current => Clone(_settings);

    public void ApplyDefaultsIfMissing(ConversationStorageSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(_settings.DiskPath) ||
            !string.IsNullOrWhiteSpace(_settings.StorageAccountName) ||
            !string.IsNullOrWhiteSpace(_settings.CosmosAccount))
        {
            return;
        }

        _settings = Clone(settings);
    }

    public void Apply(ConversationStorageSettings settings)
    {
        _settings = Clone(settings);
    }

    private static ConversationStorageSettings Clone(ConversationStorageSettings settings) => new()
    {
        Mode = settings.Mode,
        DiskPath = settings.DiskPath,
        StorageAccountName = settings.StorageAccountName,
        BlobContainerName = settings.BlobContainerName,
        CosmosAccount = settings.CosmosAccount,
        CosmosDatabase = settings.CosmosDatabase,
        CosmosContainer = settings.CosmosContainer
    };
}