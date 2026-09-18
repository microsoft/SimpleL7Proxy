# Release Notes #

2.3.0

Proxy:
* Added Request-Requeue-Delay to response with total amount of time a request was held for before retrying
* Introduce Path_xxxx= which can list hosts to use on that path
* Bug fix:  singlePass and MultiPass now work as expected
* PriorityGroups introduced to match APIM config
* RetryAfter on directMode hosts now will open the CB for that host
* Refactor Iterators code to simplify, remove a lock() from hot path
* If all hosts for a path have a open CB, retry after one of them become available
* Profiles rules Equals, NotEquals,>, >=, <, <=, between, contains, notContains, startsWith, endsWith, Regex
* Profiles can update headers based on rules
* Fix regression of stripHeaders
* bug fix: unnecessary delay at startup for profiles to show ready when not using profiles
* bug fix: acceptable codes was being obeyed in edge cases
* Bug fix: inbound key auth did not honors the header configured in `ValidateAuthConfig`
* Bug fix: users listed by `SuspendedUserConfigUrl` are now rejected with HTTP 403

Stream Parser:
* Bug fix: `AllUsage` now skips OpenAI response chunks with `usage: null` and extracts token counts from the final usage record

CompanionApp:
* Add ability to update app config
* Update deployment pages
* re-org navigation into hierarchy

Package updates:

* [Microsoft.Extensions.Configuration.AzureAppConfiguration 8.5.0 to 8.6.0](https://github.com/Azure/AppConfiguration-DotnetProvider/releases/tag/8.6.0)
* [Microsoft.Extensions.Hosting 10.0.11 to 10.0.12](https://github.com/dotnet/runtime/releases/tag/v10.0.12)
* [Microsoft.Extensions.Hosting.Abstractions 10.0.11 to 10.0.12](https://github.com/dotnet/runtime/releases/tag/v10.0.12)
* [Microsoft.Extensions.Logging.Console 10.0.11 to 10.0.12](https://github.com/dotnet/runtime/releases/tag/v10.0.12)
* [Azure.Core 1.55.0 to 1.62.0](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/core/Azure.Core/CHANGELOG.md)
* [Azure.Identity 1.20.0 to 1.21.0](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/identity/Azure.Identity/CHANGELOG.md)
* [Azure.Messaging.ServiceBus 7.20.1 to 7.20.2](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/servicebus/Azure.Messaging.ServiceBus/CHANGELOG.md)
* [Azure.Storage.Blobs 12.28.0 to 12.29.2](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/CHANGELOG.md)
* [Microsoft.IdentityModel.JsonWebTokens 8.19.1 to 8.22.0](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/releases/tag/8.22.0)
* [Microsoft.IdentityModel.Tokens 8.19.1 to 8.22.0](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/releases/tag/8.22.0)
