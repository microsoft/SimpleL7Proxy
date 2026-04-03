# StreamingMicroService - Local Development

## Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools v4 (`npm install -g azure-functions-core-tools@4`)
- Azurite (`npm install -g azurite`)

## 1. Start Azurite (local storage emulator)

Run in a separate terminal before starting the function host:

```bash
azurite --silent --location /tmp/azurite --debug /tmp/azurite-debug.log
```

Azurite listens on:
- Blob: `127.0.0.1:10000`
- Queue: `127.0.0.1:10001`
- Table: `127.0.0.1:10002`

## 2. Set Application Insights connection string

Add to `local.settings.json` under `Values`:

```json
"APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=<your-key>;IngestionEndpoint=https://..."
```

Or export it in your shell before running:

```bash
export APPLICATIONINSIGHTS_CONNECTION_STRING="InstrumentationKey=<your-key>;IngestionEndpoint=https://..."
```

## 3. Run the function

```bash
cd src/StreamingMicroService
dotnet build
func start
```

## 4. Test

```bash
curl "http://localhost:7071/api/process?url=http://localhost:3000/file/openAI.txt&processor=MultiLineAllUsage"
```
