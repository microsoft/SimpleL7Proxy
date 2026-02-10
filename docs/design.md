This document serves as the high level description of the source code.

## Overview

SimpleL7Proxy is a high-performance Layer 7 reverse proxy with intelligent backend selection. It receives incoming requests, queues them by priority, and routes them to healthy backends using configurable load balancing strategies.

### Key Capabilities

- **Multi-mode Load Balancing**: Supports round-robin, latency-based, and random backend selection
- **Path-Based Routing**: Routes requests to specific backends based on URL path matching
- **Circuit Breaker**: Automatically stops traffic to failing backends and self-heals
- **Priority Queuing**: Higher priority requests are processed before lower priority ones
- **Retry Logic**: Automatically retries failed requests against alternate backends
- **Async Mode**: Supports long-running requests via Azure Service Bus notifications

### Request Flow

```
┌─────────────┐    ┌──────────────┐    ┌────────────────┐    ┌─────────────┐
│   Client    │───►│   Server.cs  │───►│ Priority Queue │───►│ ProxyWorker │
│             │    │  (Listener)  │    │                │    │             │
└─────────────┘    └──────────────┘    └────────────────┘    └──────┬──────┘
                                                                    │
                   ┌────────────────────────────────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  Backend Selection (IteratorFactory)                                        │
│  1. Filter hosts by request path (specific vs catch-all)                    │
│  2. Create iterator based on LoadBalanceMode (roundrobin/latency/random)    │
│  3. For each host: check circuit breaker → send request → handle response   │
└─────────────────────────────────────────────────────────────────────────────┘
                   │
                   ▼
          ┌───────────────┐
          │ Backend Hosts │
          └───────────────┘
```

When deployed in Azure Container Apps, it can be set up with auto-scale to handle larger volumes. The service utilizes DNS records for each backend host; however, if DNS is not available, it can be configured with hostname and IP address combinations. Details for each request can be logged to Application Insights or an EventHub endpoint. When configured with OAuth2, the service will obtain a token for backend requests.

---

## Libraries Used

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


## Main Code Flow

| File | Purpose |
|------|---------|
| `Program.cs` | Startup, read configuration, setup .NET DI container |
| `Server.cs` | Listens for incoming requests and inserts into the priority queue |
| `Backends.cs` | Measures latencies to backends, manages host health and circuit breakers |
| `ProxyWorker.cs` | Pulls messages from the priority queue and proxies to backends |

### Backend Selection Components

| File | Purpose |
|------|---------|
| `IteratorFactory.cs` | Creates load-balanced iterators based on configuration |
| `RoundRobinHostIterator.cs` | Distributes requests evenly using a global counter |
| `LatencyHostIterator.cs` | Orders hosts by average response latency |
| `RandomHostIterator.cs` | Shuffles hosts randomly for each request |
| `SharedIteratorRegistry.cs` | Manages shared iterators for concurrent requests |
| `CircuitBreaker.cs` | Tracks failures and trips circuits for unhealthy hosts |

## Helpers

| File | Purpose |
|------|---------|
| `AppInsightsTextWriter.cs` | Sends data to console and Application Insights |
| `EventHubClient.cs` | Sends messages to EventHub |
| `PriorityQueue.cs` | Implements priority queue (highest priority exits first) |

## Runtime Objects

| File | Purpose |
|------|---------|
| `BaseHostHealth.cs` | Represents a single backend with health metrics |
| `RequestData.cs` | Data received for an incoming request |
| `ProxyData.cs` | Represents a response received from a backend |

---

## Related Documentation

- [LOAD_BALANCING.md](LOAD_BALANCING.md) - Detailed backend selection algorithm
- [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) - Circuit breaker configuration
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) - Host configuration options




