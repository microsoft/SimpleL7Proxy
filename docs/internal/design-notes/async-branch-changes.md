# Branch Walkthrough: `docs/readme-update` vs `feature/async`

This document covers all changes authored on the `docs/readme-update` branch (18 commits, 73 files). Changes are organized by functional area. Upstream merges from `feature/async` are excluded.

## Net Effect

This branch shifts the proxy from a "backup" framing to a clearer **async queue/topic architecture**, adds a **request preprocessor extension point**, splits shared parsing into its own project (`Shared-parser`), and makes **RequestAPI and container-image deployments first-class** in the orchestrator. Deployment scripts gain better error handling, global-uniqueness guidance, ACR preflight checks, and support for storage accounts with shared-key auth disabled.

---

## Source Code — Async Architecture Refactor

### Renames (BackupAPI → SBQueue / SBTopic / Serializer)

The entire "backup" concept was renamed to reflect the actual queue/topic architecture:

| Old Path | New Path |
|---|---|
| `Async/BackupAPI/BackupStatService.cs` | **Deleted** — replaced by `SBQueueService.cs` |
| `Async/BackupAPI/IBackupStatService.cs` | `Async/ServiceBus/SBQueue/ISBQueueService.cs` |
| `Async/ServiceBus/ServiceBusRequestService.cs` | `Async/ServiceBus/SBTopic/SBTopicService.cs` |
| `Async/ServiceBus/IServiceBusRequestService.cs` | `Async/ServiceBus/SBTopic/ISBTopicService.cs` |
| `DTO/RequestDataBackupService.cs` | `DTO/RequestSerializerService.cs` |
| `DTO/IRequestDataBackupService.cs` | `DTO/IRequestSerializerService.cs` |
| `DTO/NullRequestDataBackupService.cs` | `DTO/NullRequestSerializerService.cs` |

### New Files

| File | Purpose |
|---|---|
| `Async/ServiceBus/SBQueue/SBQueueService.cs` | Queue-backed async service with batching, shutdown draining, and per-minute stats. Replaces the old BackupStatService. |
| `DTO/RequestDataConverter.cs` | Version-aware request serialization/deserialization, allowing persisted payloads to evolve without breaking restore. |
| `IRequestPreprocessorPlugin.cs` | Pluggable pre-processing hook that can mutate or reject requests before validation/enqueue. |

### Key Modified Files

| File | What Changed |
|---|---|
| `Program.cs` | DI wiring updated for renamed services, new preprocessor plugin, and serializer pipeline. |
| `server.cs` | Request flow integrates the new preprocessor and renamed async services. |
| `CoordinatedShutdownService.cs` | Shutdown coordination now drains queue/topic services to avoid losing in-flight async work. |
| `HealthCheckService.cs` | Health reporting adjusted for the new service topology. |
| `Async/ServiceBus/ServiceBusFactory.cs` | Factory wiring supports the SBQueue/SBTopic split. |
| `Async/TemplateLoader.cs` | Template loading updated for new startup messaging. |
| `Async/AsyncWorkerContext.cs` | Context depends on `IRequestSerializerService` instead of the old backup service. |
| `Async/Feeder/AsyncFeeder.cs` | Feeder updated for renamed service contracts. |
| `Config/ConfigParser.cs` | Recognizes new async settings and parser split config keys. |
| `Config/ProxyConfig.cs` | Extended with new queue/topic and parser feature toggles. |
| `Constants.cs` | Updated to version `2.2.11.1`; renamed storage/service constants. |
| `RequestData.cs` | Model fields adjusted for the new serializer and preprocessor flow. |
| `Messaging/IBatchMessageTransport.cs` | Extended batch transport interface. |
| `Dockerfile`, `Dockerfile-alpine` | Build context updated for the `Shared-parser` project split. |
| `SimpleL7Proxy.csproj` | Adds `Shared-parser` project reference. |

## Source Code — Shared-Parser Split

The stream-processor code was moved from `src/Shared/` into a new `src/Shared-parser/` project to decouple parsing logic from core shared utilities:

- **New project:** `src/Shared-parser/Shared-parser.csproj` (targets .NET 10)
- **Moved files (unchanged):** `AllUsageProcessor.cs`, `BaseStreamProcessor.cs`, `CompleteAllUsageProcessor.cs`, `DefaultStreamProcessor.cs`, `IStreamProcessor.cs`, `JsonStreamProcessor.cs`, `MultiAllUsageProcessor.cs`, `OpenAIProcessor.cs`, `StreamProcessorFactory.cs`, `version.cs`
- **Modified:** `src/Shared/Shared.csproj` — removed the parser references

## Source Code — RequestAPI Function

| File | What Changed |
|---|---|
| `src/RequestAPI/deploy-flex.sh` | Build-skip optimization (reuses zip if sources unchanged); post-deploy error handler now catches DNS resolution failures in addition to host-key errors; `functions.metadata` validation added. |
| `src/RequestAPI/RequestAPI.csproj` | Updated Functions worker package versions; added `<RollForward>Major</RollForward>` for .NET runtime forward-compat. |
| `src/RequestAPI/host.json` | Host settings adjusted for new function behavior. |
| `src/RequestAPI/assignPrivs.sh` | New script for assigning RBAC permissions to the function app's managed identity on Service Bus and Cosmos DB. |

---

## Deployment Scripts

### New Files

| File | Purpose |
|---|---|
| `deployment/RequestAPI/create.sh` | Provisions the RequestAPI function app (Flex Consumption): storage account, App Insights, function app with system MI, identity-based app settings, RBAC for storage/Service Bus/Cosmos, and deployment-storage MI config. |
| `deployment/RequestAPI/deploy.sh` | Deployment wrapper that sources parameters and delegates to `src/RequestAPI/deploy-flex.sh`. |
| `deployment/ContainerImage/validate-acr.sh` | Validates ACR prerequisites (login, existence, image availability) before image deployment. |

### Orchestrator — `deployment/deploy.sh`

Added Steps 9 and 10 to the interactive menu:
- **Step 9:** Create RequestAPI Function App (`RequestAPI/create.sh`)
- **Step 10:** Deploy/Update RequestAPI (`RequestAPI/deploy.sh`)

Also made executable (`chmod +x`).

### `deployment/proxy-with-sidecar/deploy.sh`

Major rework of the pre-deployment flow:
1. **ACR image preflight** — Verifies both proxy and health-probe images exist in ACR before deploying.
2. **Placeholder app creation** — Creates a placeholder Container App (public hello-world image) to establish managed identity before the real Bicep deploy, solving the first-deploy chicken-and-egg problem with private ACR.
3. **Managed identity and AcrPull** — Explicitly enables system MI if missing, checks `AcrPull` before assigning, treats missing ACR as a hard error.
4. **Removed post-deploy role assignment** — Identity and role are now guaranteed before deploy.

### `deployment/ContainerImage/deploy.sh` (renamed from `build.sh`)

Adds: (1) ensures resource group exists; (2) ensures ACR exists, creating it with `ACR_SKU` if needed; (3) builds and pushes the **health-probe image** alongside the proxy image.

### `deployment/BlobStorage/deploy.sh`

- Storage account creation now includes `--public-network-access Enabled`.
- Container creation switched from ARM (`az storage container-rm`) to data-plane with `--auth-mode login`, with JIT `Storage Blob Data Contributor` role assignment and 30s RBAC propagation wait. Works whether or not shared-key auth is enabled.
- Improved error messaging for unavailable storage account names.

### `deployment/AppConfiguration/deploy.sh`

- App Config creation now captures errors to detect `NameUnavailable` and prints guidance on global uniqueness with a suggested suffix.

### `deployment/RequestAPI/create.sh`

- Storage account creation includes `--public-network-access Enabled` and `--min-tls-version TLS1_2` to prevent Kudu deployment failures when Azure defaults `publicNetworkAccess` to `Disabled`.

### Parameter Examples & Mode Changes

| File | Change |
|---|---|
| `deployment/deploy.parameters.example.sh` | Added `ACR_SKU`, RequestAPI parameters; global-uniqueness comments. |
| `deployment/AppConfiguration/deploy.parameters.example.sh` | Global-uniqueness comment on `APPCONFIG_NAME`. |
| `deployment/BlobStorage/deploy.parameters.example.sh` | Global-uniqueness comment on `STORAGE_ACCOUNT_NAME`. |
| `deployment/ContainerImage/deploy.parameters.example.sh` | Renamed from `build.parameters.example.sh`. |
| `deployment/DNS/deploy.sh`, `VNet/deploy.sh`, `Prereq/validate.sh` | Made executable (`chmod +x`). No logic changes. |

---

## Documentation

| File | What Changed |
|---|---|
| `deployment/README.md` | Updated step descriptions for the new deployment flow. |
| `deployment/README.new.md` | Added global-uniqueness notes, `[!NOTE]` callouts, `ACR_SKU` documentation, ACR auto-creation in Step 3, name-availability verification commands. |
| `deployment/DAY2_OPERATIONS.md` | Refreshed for new runtime and deployment expectations. |
| `deployment/BlobStorage/README.md` | Updated RBAC and container-creation descriptions to reflect `--auth-mode login` approach. |
| `deployment/ContainerImage/README.md` | Updated for rename from build to deploy, ACR auto-creation, health-probe image. |
| `ReleaseNotes/version2.2.md` | Updated for 2.2.11.1. |

## Other

| File | What Changed |
|---|---|
| `.gitignore` | Added entry. |
| `src/SimpleL7Proxy/config.json` | Minor config update. |
| `test/curl/SimpleL7TriggerAsync.sh` | Test script update. |
| `test/curl/sample.txt` | Added sample test data. |
| `test/openai/call-proxy.sh`, `demo-aca-request.sh` | Test script updates. |
| `test/generator/generator_one/appsettings.json` | Generator config update. |

## Commits

| Hash | Message |
|---|---|
| `b45c1a5` | migrate backupAPI to the messagePump |
| `131ce95` | implement incoming request plugin |
| `a6ab660` | rename build.*.sh to deploy.*.sh |
| `257b38d` | rename |
| `0c4e7d0` | rename BackupAPI to SBQueue |
| `69edc45` | rename ServiceBusRequestService to SBTopicService |
| `c6aa064` | move to new folders |
| `c43129a` | change namespaces for SBTopic and SBQueue |
| `39837e3` | deserialize from blob as a stream rather than converting to UTF16 and then parsing |
| `c13c6aa` | Rename the requestbackup to SerializerService |
| `738520e` | updates |
| `e3f6537` | move the shared parser code into its own dotnet 10 shared folder |
| `8bfb378` | Updates to deploy functions |
| `e3bd905` | Add RequestAPI to interactive deploy |
| `8781f9b` | add Shared-Parser |
| `e711755` | update to 2.2.11.1 |
| `f3daaea` | Update deployment scripts and documentation |
| `7852572` | Update deployment scripts and documentation |
