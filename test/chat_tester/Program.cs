using chat_tester.Components;
using chat_tester.Components.Shared;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("chat-models.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"chat-models.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<AuthTokenSettings>();
builder.Services.AddSingleton<UserSettings>();
builder.Services.AddSingleton<HeaderSettings>();
builder.Services.AddSingleton<HistorySettings>();
builder.Services.AddSingleton<RequestDebugSettings>();
builder.Services.AddSingleton<ModelDefaults>();
builder.Services.AddSingleton<ChatHistoryStore>();
builder.Services.Configure<ChatTesterOptions>(
    builder.Configuration.GetSection(ChatTesterOptions.SectionName));

var app = builder.Build();
app.Services.GetRequiredService<HistorySettings>()
    .ApplyDefaultsIfMissing(app.Services.GetRequiredService<IOptions<ChatTesterOptions>>().Value.History);
await app.Services.GetRequiredService<ChatHistoryStore>().ReloadAsync();

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
