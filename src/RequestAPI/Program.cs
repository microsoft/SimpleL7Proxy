using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Register ServiceBus extension by ensuring Services are correctly configured
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Additional logging to help diagnose Service Bus trigger issues
builder.Services.AddLogging(loggingBuilder => 
{
    loggingBuilder.AddConsole();
    loggingBuilder.AddFilter("Azure.Messaging.ServiceBus", LogLevel.Debug);
    loggingBuilder.AddFilter("Microsoft.Azure.Functions.Worker.ServiceBus", LogLevel.Debug);
});

// Build and run the function app
var app = builder.Build();
app.Run();
