using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Worker>();
var host = builder.Build();
await host.RunAsync();

public class Worker
{
}