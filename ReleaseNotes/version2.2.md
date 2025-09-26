# Release Notes #
## 2.2.7

Policy:
* Implement session affinity via header: x-backend-affinity
* Bug fix when backends are throttled

Proxy:
* Implement background task lookup
* code refactor async processor to offload work to ProxyWorker
* Fix logging issues
* Fix for background requests

## v2.2.6

API:
* Added feeder to refeed requests that were in a NEEDS TO BE PROCESSED state.
* Processor looks for events on the service bus

Proxy:
* Backup request meta data on shutdown
* Remove backups once request is complete.
* Re-feed incoming requests on the service bus
* Fix gemini usage stats regular expression
* Send update status on service bus


## v2.2.5

API:
* Created initial version of API with support for new, update

Proxy:
* added ProfileUserID to requestData
* Track requests by calling the API for new and completed requests
* Added BackupAPI Service 
* Add code to cancel async operation and update status to NEEDS TO BE REPROCESSED on shutdown

## V2.2.4.p5

Proxy:
* bug fix: token processing issues for multiline processor
* Implement backup and delete for incoming requests into blob storage using DTO model.

## V2.2.4.p4

Repo:
* Documentation updates

Proxy:
* Consolidated async profile configuration into a single filed:  AsyncClientConfigFieldName
* Removed unused configuration: AsyncSBStatusWorkers
* Updated documentation explainations
* Bug fix for 404 exception
* Refactor stream processor selector
* Added multi line all token processor 

Policy:
* Remove unused variables
* Added x-ms-client-request-id to help debug with OpenAI in the future


## V2.2.4.p3
Proxy:
* Performance improvements in stream parsing
* Uncomment parsing code 


## V2.2.4.p2
Proxy:
* Bug Fix: Missing response headers
* Bug Fix: Missing content when TOKENPROCESSOR was unknown
* Implement batch processing for service bus events, ensure in order upload of events 
* Add AsyncProcessingError to servicebus in case of error
* Add x-Async-Error to response headers in case of processing error
* Log Exceptions for failure to create Blob during async processing
* Drain upto 50 SB events at a time instead of a single event.
* Implement AllUsageProcessor, refactor code to simplyfy creating future parsers
* Remove < 10ms delay for async startup
* Calculate delay based on enqueue time to when triggering async



## V2.2.4.p1

* Bug Fix: User UserProfileHeader rather than UserID when looking up asyncmode
* Bug Fix: log Completion-Tokens instead of CompletionTokens, Prompt_Tokens.... and Total_Tokens...
* Bug Fix: Correct text in async response to use service bus instead of event hub.

## V2.2.4
Proxy:
* Bug fix: for server disconnects without sending Content-Length header.
* Bug fix: Disable async mode on initialization failure.
* Warning: Fix compiler warnings
* Refactor code to use the factory model for BlobStorage and AsyncWorker
* Added ability to connect BlobStorage via Managed Identity
* Added ability to conenct to ServiceBus via Managed Identity
* Added validator for service bus topic config
* Bug fixes for async operation
* Flush the SBStatus messages on shutdown, added AsyncProcessing and AsyncProcessed
* Updated code to distinguish LogInformation, LogWarning and LogCritical keyed off of LOG_LEVEL
* Added a token parser for OpenAI, triggered via response headers

Policy:
* Bug fix for edge cases where priority was not found and retries was set to 1
* Enhancement: add buffer-response="false" to enable streaming
* Added TOPENPROCESSOR header in the response to enable token processing in the proxy

