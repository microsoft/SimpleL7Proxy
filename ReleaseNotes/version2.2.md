# Release Notes #

Proxy:
* Bug fix: for server disconnects without sending Content-Length header.
* Bug fix: Disable async mode on initialization failure.
* Warning: Fix compiler warnings
* Refactor code to use the factory model for BlobStorage and AsyncWorker
* Added ability to connect BlobStorage via Managed Identity
* Added ability to conenct to ServiceBus via Managed Identity
* Added validator for service bus topic config
* Bug fixes for async operation

Policy:
* Bug fix for edge cases where priority was not found and retries was set to 1
* Enhancement: add buffer-response="false" to enable streaming
