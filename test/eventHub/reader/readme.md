# Event Hub Reader

A .NET console application that reads events from an Azure Event Hub. Supports both connection string and managed identity (DefaultAzureCredential) authentication.

## Prerequisites

- .NET 10.0 SDK
- An Azure Event Hub

## Environment Variables

| Variable | Required | Description |
|---|---|---|
| `EVENTHUB_NAME` | Yes | Name of the Event Hub |
| `EVENTHUB_NAMESPACE` | No* | Event Hub namespace (short name or FQDN) |
| `EVENTHUB_CONNECTIONSTRING` | No* | Full connection string for the Event Hub namespace |
| `EVENTHUB_CONSUMER_GROUP` | No | Consumer group name (defaults to `$Default`) |

\* Provide either `EVENTHUB_CONNECTIONSTRING` or `EVENTHUB_NAMESPACE` for authentication.

## Usage

### Using Managed Identity (DefaultAzureCredential)

```bash
export EVENTHUB_NAME=<your-eventhub-name>
export EVENTHUB_NAMESPACE=<your-namespace>

dotnet run
```

### Using a Connection String

```bash
export EVENTHUB_NAME=<your-eventhub-name>
export EVENTHUB_CONNECTIONSTRING="Endpoint=sb://<your-namespace>.servicebus.windows.net/;SharedAccessKeyName=<key-name>;SharedAccessKey=<key>"

dotnet run
```

## Summary Mode

Use the `-s` flag to enable summary mode, which aggregates event counts by `Type` and prints a summary every 10 seconds instead of printing each event individually:

```bash
dotnet run -- -s
```
