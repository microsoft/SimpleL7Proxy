This document serves as the high level description of the source code.

Overview:

The service receives incoming requests and retransmits it to the lowest latency backend.  If the backend fails, the service will retry the request against the next lowest latency backend.  When deployed in Azure Container Apps, it can be setup with auto-scale to allow it handle larger volumes. The service utilizes the DNS record for each backend host, however in the case that DNS is not available, it can be configured with the hostname and IP address combination as well.  Details for each request can be logged to application insights or an EventHub endpoint.  When configured with Oauth2, the service will obtain a token for the backend requests. 



Libraries used:

Purpose:  Authenticate to Azure and acquire Oauth2 tokens. 
* Azure.Core: Provides essential classes and utilities for working with Azure services, including HTTP pipeline configuration, retry policies, and diagnostics.
* Azure.Identity: Offers a variety of token credential implementations for authenticating Azure SDK clients using Azure Active Directory (AAD). It supports multiple authentication methods, including managed identities, service principals, and interactive user authentication.

Purpose:  Log messages to application insights and EventHub
* Azure.Messaging.EventHubs: Facilitates the sending and receiving of messages to and from Azure Event Hubs
* Azure.Messaging.EventHubs.Processor 
* Microsoft.ApplicationInsights: Core library for Application Insights, which allows you to monitor and analyze telemetry data from your applications to improve performance and usability.

* Microsoft.ApplicationInsights.AspNetCore: Integrates Application Insights with ASP.NET Core applications, enabling automatic collection of telemetry data such as requests, dependencies, and exceptions.

* Microsoft.ApplicationInsights.DependencyCollector: Automatically collects dependency telemetry data, such as HTTP requests and SQL queries, to help you understand the performance and reliability of your application's dependencies.
* Microsoft.ApplicationInsights.WorkerService: Integrates Application Insights with .NET Core worker services, enabling telemetry collection for background services and long-running processes.


Purpose: Provide .NET service and dependency injection. 
* Microsoft.Extensions.DependencyInjection: rovides a set of abstractions and a default implementation for dependency injection, allowing you to manage the lifetime and dependencies of your services.
* Microsoft.Extensions.Hosting: Offers a set of abstractions for hosting applications, including support for running background services and handling application lifetime events.
* Microsoft.Extensions.Http: Adds extensions for configuring and using HttpClient instances with dependency injection, including support for named clients and typed clients.
* Microsoft.Extensions.Logging: Provides a logging framework with support for various logging providers, enabling you to log messages from your application in a consistent and configurable manner.

* Microsoft.Extensions.Options: Supports the configuration and management of options and settings in your application, including support for validating and reloading options.


Main Code flow:

Program.cs : Startup , read configuration, setup the .NET framework service
Server.cs : listens for incoming requests and inserts into the priority queue
backends.cs : measures the latencies to the backends and orders the hosts based on success rate.
proxyWorker.cs: pulls a message from the priority queue and proxies the request to the best backend

Helpers:
AppInsightsTextWriter.cs : sends 1 copy of the data to the console and another copy to app insights
EventHubClient.cs : sends a message into the eventhub
PriorityQueue: implements a priority queue, highest priority exits first

Runtime Objects:
Backend.cs : represents a single backend
RequestData.cs : data received for an incoming request
ProxyData.cs : represents a response received from a backend



