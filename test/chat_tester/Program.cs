using chat_tester.Components;
using chat_tester.Components.Shared;
using chat_tester.Components.Shared.EventHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("chat-models.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"chat-models.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("vision-models.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"vision-models.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

var eventHubSection = builder.Configuration.GetSection(EventHubMonitorOptions.SectionName);
var eventHubEnabled = eventHubSection.GetValue<bool>("eventhub_enabled", true);
var localEventFilePath = eventHubSection.GetValue<string>("LocalFilePath");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddDataProtection()
    .SetApplicationName("chat_tester")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")));
builder.Services.AddSingleton(new HttpClient
{
    Timeout = TimeSpan.FromMinutes(5)
});
builder.Services.AddSingleton<AuthTokenSettings>();
builder.Services.AddSingleton<UserSettings>();
builder.Services.AddSingleton<HeaderSettings>();
builder.Services.AddSingleton<HistorySettings>();
builder.Services.AddSingleton<ConversationSettings>();
builder.Services.AddSingleton<RequestDebugSettings>();
builder.Services.AddSingleton<AutoCollapseSettings>();
builder.Services.AddSingleton<ModelDefaults>();
builder.Services.AddSingleton<VisionModelCatalog>();
builder.Services.AddSingleton<ChatHistoryStore>();
builder.Services.AddSingleton<ChatConversationStore>();
builder.Services.AddSingleton<EventHubMonitorStore>();
builder.Services.AddSingleton<ProxyMetricsCatalog>();
if (eventHubEnabled || !string.IsNullOrWhiteSpace(localEventFilePath))
{
    builder.Services.AddHostedService<EventHubReader>();
}
builder.Services.AddScoped<UserPreferencesService>();
builder.Services.Configure<ChatTesterOptions>(
    builder.Configuration.GetSection(ChatTesterOptions.SectionName));
builder.Services.Configure<EventHubMonitorOptions>(
    builder.Configuration.GetSection(EventHubMonitorOptions.SectionName));

var app = builder.Build();
var chatTesterOptions = app.Services.GetRequiredService<IOptions<ChatTesterOptions>>().Value;
app.Services.GetRequiredService<HistorySettings>()
    .ApplyDefaultsIfMissing(chatTesterOptions.History);
app.Services.GetRequiredService<ConversationSettings>()
    .ApplyDefaultsIfMissing(chatTesterOptions.Conversations);
await app.Services.GetRequiredService<ChatHistoryStore>().ReloadAsync();
await app.Services.GetRequiredService<ChatConversationStore>().ReloadAsync();

var proxyMetricsCatalog = app.Services.GetRequiredService<ProxyMetricsCatalog>();

static ParsedEventRecord SeedRequest(string mid, string type, int status, string path, string modelKey, string model, string backendHost)
    => new(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = type,
        ["MID"] = mid,
        ["Status"] = status.ToString(),
        ["Path"] = path,
        [modelKey] = model,
        ["Backend-Host"] = backendHost,
    });

proxyMetricsCatalog.Publish(new List<ParsedEventRecord>
{
    // Server + fleet health (drives the Server and Backends metric groups).
    new(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = "S7P-Backend",
        ["Date"] = DateTimeOffset.UtcNow.ToString("O"),
        ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
        ["Ver"] = "9.0.0-preview-ui",
        ["LoadBalanceMode"] = "latency",
        ["ActiveHostsCount"] = "3",
        ["CPU-Usage"] = "38%",
        ["Memory-Usage"] = "1.2 GB",
        ["Open-Connections"] = "642",
        ["ThreadPoolSaturation"] = "41%",
        ["Response-Content-Length"] = "1984",
        ["1-Host"] = "https://backend-a.contoso.net",
        ["1-Status"] = "active",
        ["1-Latency"] = "112",
        ["2-Host"] = "https://backend-b.contoso.net",
        ["2-Status"] = "active",
        ["2-Latency"] = "127",
        ["3-Host"] = "https://backend-c.contoso.net",
        ["3-Status"] = "throttled",
        ["3-Latency"] = "249",
    }),
    // Endpoint sample (drives the Endpoints metric group).
    new(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Method"] = "POST",
        ["Path"] = "/chat/completions",
        ["Uri"] = "https://proxy.contoso.net/chat/completions",
        ["RequestType"] = "chat",
        ["RequestHost"] = "proxy.contoso.net",
        ["Total-Latency"] = "285",
        ["Request-Queue-Duration"] = "18",
        ["Connection-Establishment-Time"] = "14",
    }),
    // Sample request events (drive the Request and Models metric groups).
    SeedRequest("mid-001", "S7P-ProxyRequest", 200, "/chat/completions", "Model", "gpt-4o-mini", "https://backend-a.contoso.net"),
    SeedRequest("mid-002", "S7P-ProxyRequest", 200, "/chat/completions", "DeploymentName", "gpt-4o", "https://backend-b.contoso.net"),
    SeedRequest("mid-003", "S7P-ProxyRequest", 429, "/embeddings", "Model", "text-embedding-3-large", "https://backend-c.contoso.net"),
    SeedRequest("mid-004", "S7P-ProxyRequestRequeued", 503, "/responses", "ModelDeployment", "gpt-4.1-mini", "https://backend-c.contoso.net"),
    SeedRequest("mid-005", "S7P-CircuitBreakerError", 503, "/chat/completions", "Model", "gpt-4o-mini", "https://backend-b.contoso.net"),
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
