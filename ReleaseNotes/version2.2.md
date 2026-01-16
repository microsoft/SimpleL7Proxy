# Release Notes #

Proxy:
* Added CheckingBackgroundRequestStatus and BackgroundRequestSubmitted as a status for background requests
* Fix bugs related to background request lifecycle
* Bug fixes for async processed request status updates
* Added support to connect to EventHub via managed identity
* Added configurable delay to wait for EventHub startup
* Refactor configuration startup to use reflection
* Update client library versions for Core, Identity, ServiceBus, BlobStorage and DI
* Minor perf improvement parsing headers
* Added consolidated AsyncSBConfig env var
* Added check for unauthorized access during startup
* Enhanced logging in debug level
* Make the SAS generation configurable in the user profile, default to off
* Added a blob worker queue to reduce resource contention
* Refactored code to utilize the blob workers 
* Capture backend config errors
* Fix circuit breaker usage, add CB stats to health
* Streamline blob creation until needed
* Add AsyncTTLSecs as config parameter
* Improve performance of probe requests
* Capture error codes from inner exception on HttpException
* Don't read the body for probe queries to the backends
* Bug fix for null eventhub client
* Streamline memory used by event objects
* Add streamlined probe server with ability to run as side car
* Reworked ProxyWroker to match the response flow from the 2.1.x release
* Track HTTP response in proxy data and dispose after delivery to client 

Policy:
* Update backend logs for better readability
* Created a V2 policy for readability improvements
* Added section to return 408 on timeout, 429 on concurrencyLimits, and on 400 return response body

## 2.2.8.p1

Proxy:
* Added CheckingBackgroundRequestStatus and BackgroundRequestSubmitted as a status for background requests
* Fix bugs related to background request lifecycle

Policy:
* Update backend logs for better readability
* Created a V2 policy for readability improvements

## 2.2.8

Policy:
* Bug fix for 404 getting returned as a 429
* Track PolicyCycleCounter across requests

Proxy:
* Add configuration for DependancyHeaders, a list of headers to copy into the response
* Bug fixes for background request processing
* Track PolicyCycleCounter across requests
* Track BackendAttempts and DownstreamAttempts seperately


## 2.2.7.P1
Policy:
* Added flow diagram
* add response code to log
  
Proxy:
* Bug fixes for logging to Type: S7P-ProxyRequest
* Bug fixes for parsing gemini-2.5-pro 
* Logging changes to differentiate startup from runtime
* Refactor and move config out of backends and into cofig directory
* Refactor request cleanup into RequestData class and proxyworker for readability
* Fix status code updates for background jobs
* Bug fix for 421 that was not ending up in S7P-ProxyRequest logs

Test:
* added test cases for gemini-2.5, gemini-2.5-pro, gemini-2.5-pro-streaming
* Refactor to make it easier to add test cases
 
RequestAPI:
* removed New, NewBulk, Update 

Docs:
* Updated policy docs link
  
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

