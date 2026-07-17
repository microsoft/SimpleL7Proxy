namespace chat_tester.Components.Shared;

public sealed class HistorySettings
{
    private HistoryStorageSettings _settings = new();

    public HistoryStorageSettings Current => Clone(_settings);

    public void ApplyDefaultsIfMissing(HistoryStorageSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(_settings.DiskPath) ||
            !string.IsNullOrWhiteSpace(_settings.StorageAccountName) ||
            !string.IsNullOrWhiteSpace(_settings.CosmosAccount))
        {
            return;
        }

        _settings = Clone(settings);
    }

    public void Apply(HistoryStorageSettings settings)
    {
        _settings = Clone(settings);
    }

    private static HistoryStorageSettings Clone(HistoryStorageSettings settings) => new()
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