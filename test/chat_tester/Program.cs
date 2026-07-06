using chat_tester.Components;
using chat_tester.Components.Shared;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("chat-models.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"chat-models.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("vision-models.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"vision-models.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
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
builder.Services.AddScoped<UserPreferencesService>();
builder.Services.Configure<ChatTesterOptions>(
    builder.Configuration.GetSection(ChatTesterOptions.SectionName));

var app = builder.Build();
var chatTesterOptions = app.Services.GetRequiredService<IOptions<ChatTesterOptions>>().Value;
app.Services.GetRequiredService<HistorySettings>()
    .ApplyDefaultsIfMissing(chatTesterOptions.History);
app.Services.GetRequiredService<ConversationSettings>()
    .ApplyDefaultsIfMissing(chatTesterOptions.Conversations);
await app.Services.GetRequiredService<ChatHistoryStore>().ReloadAsync();
await app.Services.GetRequiredService<ChatConversationStore>().ReloadAsync();

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
