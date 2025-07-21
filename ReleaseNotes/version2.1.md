# V2.1.33.p2 Release Notes#

The following changs were implemented in this release.

Proxy:
* "Reason" element added as a english representation of the Status field to Proxy Requests.
* Default value of LogPoller and LogProbes set to true, override with environment variables: LogProbes and LogPoller
* Bug Fix: Incoming HTTP Probe's were causing An extra event to be logged as S7P-Console
* Enhancement: Update the text for internal worker states 
* Enhancement: Status code 412 is now treated as a transient error and will get retried with the next backend host.

Policy:
* Bug Fix: Retry Count is now updated in the case of a timeout or concurrency limit overrun.
* Enhancement:  When retries are exhausted, return status 412.
* Bug Fix: The retry loop was using a hard coded 50 to for all retries, regardless of the count set at the priority level.

Null Server:
* Enhancement: Added a server responding on port 3001
* Enhancement: Added a /412error route code to help with testing

# V2.1.33.p1 Release Notes

Proxy: 
* Bug Fix: turn off log_to_file


# V2.1.32
* Enhancement: Internal worker states are not logged in case of incomplete proxy requests.
* Enhancement: Default logPoller and LogProbes to off.

# V2.1.31

Readme:
* Enhancement: Update and move content from Readme into seperate markdown documents.

Proxy:
* Bug Fix:  Remove redundant check for logging to console
* Enhancement: Remove Host-ID from events, which already appears multiple times
* Enhancement: Changed json host status to a format more compatible with DataDog
* Enhancement: Added ParentId to Events for easier tracking with Distributed Event Tracking in Application Insights
* Enhancement: Added LogDependency to track backend requests seperately
* Bug Fix:  Make logging of different event types be triggered by LogProbes, LogConsole and LogDependency
* Enhacement: Add Duration to logged Events.
* Enhancement: Add LogDependency and logRequests to event logs
* Enhancement: Change LookupHeaderName to UserIDFieldName, make it backward compatible
* Enhancement: Remove propigation for fields in userProfiles that begin with internal-
* Enhancement: Implement Distributed Tracking

Policy: 
* BugFix: Initialize backendCallCounter to 0
* Enhancement: Add a default timeout of 300 seconds for backend requests.
* BugFix: Change default value for ShouldLimit to "Off" instead of false.

Delay Functions:
* Enhancement: Add delay-function for timeout testing

Markdown:
* Enhancement: Updated documentation with: how-it-works.md, cost-optimization-scenario, financial-services-scenario, high-availability-scenario

NullServer:
* Enhancement: Added /500error, /killConnection, /delay800Seconds, /429error  for test scenarios
