using Azure.Identity;
using CompanionApp.Components;
using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using CompanionApp.Startup;
using CompanionApp.Tokenomics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Shared.Auth;
using SimpleL7Proxy.StreamProcessor;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    public static int DEBUGLEVEL =0;

    internal static readonly StringBuilder checkpoints = new StringBuilder("InitializeAsync: entered");
    static string lastStage = "entered";

    internal static void checkpointHelper(string action, string stage)
    {
        if (DEBUGLEVEL == 1 && lastStage != stage)
        {
            checkpoints.Append($" Entering stage: {stage}");
        } else if (DEBUGLEVEL >= 2)
        {
            checkpoints.Append($"; {action} ");
        }
        lastStage = stage;
    }

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "{Checkpoint}")]
    internal static partial void LogCheckpoint(ILogger logger, string checkpoint);

    public static async Task Main(string[] args)
    {
        
        TokenomicsReplayOptions replayOptions;
        try
        {
            replayOptions = ParseCommandLineArguments(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
            return;
        }

        var uiOnly = replayOptions.ApplicationArgs.Contains("--uionly", StringComparer.OrdinalIgnoreCase);
        var applicationArgs = replayOptions.ApplicationArgs
            .Where(arg => !string.Equals(arg, "--uionly", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var builder = WebApplication.CreateBuilder(applicationArgs);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = "custom");
        builder.Logging.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
        await CompanionAppStartup.AddModelConfigFiles(builder);
        StartupDI(builder, replayOptions, uiOnly);
        var app = builder.Build();
        var authenticationSection = app.Configuration.GetSection("CompanionApp:Authentication");
        if (authenticationSection.GetValue<bool>("Enabled"))
        {
            app.Logger.LogInformation(
                "Authentication enabled: provider {Provider}.",
                authenticationSection["Provider"] ?? "Unknown");
        }
        else
        {
            app.Logger.LogWarning("Authentication disabled: Companion App is unauthenticated.");
        }

        if (uiOnly)
        {
            app.Logger.LogInformation(
                "UI-only mode: profile storage check and configuration loading are disabled.");
        }
        else
        {
            var (drafts, defaultedProfileKeys) = await StartupAppConfig.InitializeAsync(app);
            await StartupStorageBlob.CheckProfileStorageAsync(app);
            await StartupStorageBlob.CreateDefaultProfileBlobsAsync(app, drafts, defaultedProfileKeys);
        }
        await CompanionAppStartup.InitializeAsync(app);
        StartupHttpPipeline(app);
        app.Run();
    }

    private static void StartupDI(
        WebApplicationBuilder builder,
        TokenomicsReplayOptions replayOptions,
        bool uiOnly)
    {
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.AddDataProtection()
            .SetApplicationName("chat_tester")
            .PersistKeysToFileSystem(
                new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")));

        var authenticationSection = builder.Configuration.GetSection("CompanionApp:Authentication");
        var authenticationEnabled = authenticationSection.GetValue<bool>("Enabled");
        var configuredAuthenticationProvider = authenticationSection["Provider"] ?? "None";
        var authenticationProvider = configuredAuthenticationProvider.ToLowerInvariant() switch
        {
            "none" => "None",
            "oauth2" => "OAuth2",
            "github" => "GitHub",
            "google" => "Google",
            _ => configuredAuthenticationProvider
        };
        if (authenticationEnabled && authenticationProvider.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CompanionApp authentication is enabled, but no provider is selected.");
        }

        if (authenticationEnabled)
        {
            const string cookieScheme = "CompanionAppCookie";
            var loginUrl = authenticationSection["LoginUrl"] ?? "/login";
            var unauthorizedRedirectUrl = authenticationSection["UnauthorizedRedirectUrl"] ?? "/access-denied";
            var authenticationBuilder = builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = authenticationProvider.Equals("OAuth2", StringComparison.OrdinalIgnoreCase)
                    ? "OAuth2"
                    : cookieScheme;
                options.DefaultChallengeScheme = authenticationProvider;
            });

            authenticationBuilder.AddCookie(cookieScheme, options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(
                    Math.Max(1, authenticationSection.GetValue<int?>("SessionTimeoutMinutes") ?? 60));
                options.SlidingExpiration = authenticationSection.GetValue<bool?>("SlidingExpiration") ?? true;
                options.LoginPath = loginUrl;
                options.AccessDeniedPath = unauthorizedRedirectUrl;
            });

            switch (authenticationProvider.ToLowerInvariant())
            {
                case "oauth2":
                    var oauthAudiences = authenticationSection.GetSection("OAuth2:Audiences").Get<string[]>() ?? [];
                    var oauthRequiredClaims = authenticationSection.GetSection("OAuth2:RequiredClaims")
                        .Get<Dictionary<string, string>>() ?? new(StringComparer.OrdinalIgnoreCase);
                    var oauthConfig = new StringBuilder()
                        .Append("enabled=true;mode=oauth2")
                        .Append(";header=").Append(authenticationSection["OAuth2:Header"] ?? "Authorization")
                        .Append(";issuer=").Append(authenticationSection["OAuth2:Issuer"])
                        .Append(";metadataaddress=").Append(authenticationSection["OAuth2:MetadataAddress"])
                        .Append(";audiences=[").AppendJoin(',', oauthAudiences).Append(']')
                        .Append(";validateissuer=").Append(authenticationSection.GetValue<bool?>("OAuth2:ValidateIssuer") ?? true)
                        .Append(";validateaudience=").Append(authenticationSection.GetValue<bool?>("OAuth2:ValidateAudience") ?? true)
                        .Append(";requiresignedtokens=").Append(authenticationSection.GetValue<bool?>("OAuth2:RequireSignedTokens") ?? true)
                        .Append(";validatelifetime=").Append(authenticationSection.GetValue<bool?>("OAuth2:ValidateLifetime") ?? true)
                        .Append(";clockskewminutes=").Append(authenticationSection.GetValue<int?>("OAuth2:ClockSkewMinutes") ?? 1);
                    foreach (var requiredClaim in oauthRequiredClaims)
                    {
                        oauthConfig.Append(";requiredclaim=")
                            .Append(requiredClaim.Key).Append(':').Append(requiredClaim.Value);
                    }

                    var oauthValidator = new IncomingAuthValidator();
                    oauthValidator.Parse(oauthConfig.ToString());
                    authenticationBuilder.AddJwtBearer("OAuth2", options =>
                    {
                        options.TokenValidationParameters = oauthValidator.ToTokenValidationParameters();
                        options.Events.OnMessageReceived = context =>
                        {
                            if (!oauthValidator.Header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                                && context.Request.Headers.TryGetValue(oauthValidator.Header, out var token))
                            {
                                var headerValue = token.ToString();
                                context.Token = headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    ? headerValue["Bearer ".Length..].Trim()
                                    : headerValue.Trim();
                            }

                            return Task.CompletedTask;
                        };
                        options.Events.OnTokenValidated = context =>
                        {
                            var subject = context.Principal?.FindFirstValue("sub")
                                ?? context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                                ?? "unknown";
                            context.HttpContext.RequestServices
                                .GetRequiredService<ILoggerFactory>()
                                .CreateLogger("CompanionApp.Authentication")
                                .LogInformation("User authenticated: {User} via OAuth2.", subject);
                            return Task.CompletedTask;
                        };
                        options.Events.OnChallenge = context =>
                        {
                            if (context.Request.Headers.Accept.Any(value =>
                                    value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
                                && !context.Request.Path.Equals(loginUrl, StringComparison.OrdinalIgnoreCase))
                            {
                                context.HandleResponse();
                                context.Response.StatusCode = StatusCodes.Status302Found;
                                context.Response.Headers.Location = loginUrl;
                            }

                            return Task.CompletedTask;
                        };
                    });
                    break;
                case "github":
                    if (string.IsNullOrWhiteSpace(authenticationSection["GitHub:ClientId"])
                        || string.IsNullOrWhiteSpace(authenticationSection["GitHub:ClientSecret"]))
                    {
                        throw new InvalidOperationException("GitHub authentication requires a client ID and client secret.");
                    }

                    authenticationBuilder.AddOAuth("GitHub", options =>
                    {
                        options.SignInScheme = cookieScheme;
                        options.ClientId = authenticationSection["GitHub:ClientId"] ?? string.Empty;
                        options.ClientSecret = authenticationSection["GitHub:ClientSecret"] ?? string.Empty;
                        options.CallbackPath = authenticationSection["GitHub:CallbackPath"] ?? "/signin-github";
                        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                        options.UserInformationEndpoint = "https://api.github.com/user";
                        options.SaveTokens = false;
                        var scopes = (authenticationSection.GetSection("GitHub:Scopes").Get<string[]>() ?? []).ToList();
                        var organizationRestrictions = authenticationSection
                            .GetSection("GitHub:AllowedOrganizations").Get<string[]>() ?? [];
                        var teamRestrictions = authenticationSection.GetSection("GitHub:AllowedTeams").Get<string[]>() ?? [];
                        if ((organizationRestrictions.Length > 0 || teamRestrictions.Length > 0)
                            && !scopes.Contains("read:org", StringComparer.OrdinalIgnoreCase))
                        {
                            scopes.Add("read:org");
                        }

                        foreach (var scope in scopes)
                        {
                            options.Scope.Add(scope);
                        }

                        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
                        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
                        options.ClaimActions.MapJsonKey("urn:github:avatar", "avatar_url");
                        options.Events.OnCreatingTicket = async context =>
                        {
                            using var userRequest = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                            userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                            userRequest.Headers.UserAgent.ParseAdd("SimpleL7Proxy-CompanionApp");
                            userRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            using var userResponse = await context.Backchannel.SendAsync(
                                userRequest,
                                HttpCompletionOption.ResponseHeadersRead,
                                context.HttpContext.RequestAborted);
                            userResponse.EnsureSuccessStatusCode();
                            using var userDocument = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
                            context.RunClaimActions(userDocument.RootElement);
                            var login = userDocument.RootElement.GetProperty("login").GetString() ?? string.Empty;
                            var allowedUsers = authenticationSection.GetSection("GitHub:AllowedUsers").Get<string[]>() ?? [];
                            var allowedOrganizations = authenticationSection.GetSection("GitHub:AllowedOrganizations").Get<string[]>() ?? [];
                            var allowedTeams = authenticationSection.GetSection("GitHub:AllowedTeams").Get<string[]>() ?? [];

                            var isAllowedUser = allowedUsers.Contains(login, StringComparer.OrdinalIgnoreCase);
                            var hasOrganizationAccess = false;
                            if (allowedOrganizations.Length > 0)
                            {
                                using var organizationRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/orgs?per_page=100");
                                organizationRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                                organizationRequest.Headers.UserAgent.ParseAdd("SimpleL7Proxy-CompanionApp");
                                using var organizationResponse = await context.Backchannel.SendAsync(
                                    organizationRequest,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    context.HttpContext.RequestAborted);
                                organizationResponse.EnsureSuccessStatusCode();
                                using var organizations = JsonDocument.Parse(await organizationResponse.Content.ReadAsStringAsync());
                                hasOrganizationAccess = organizations.RootElement.EnumerateArray()
                                    .Select(item => item.GetProperty("login").GetString())
                                    .Any(name => allowedOrganizations.Contains(name, StringComparer.OrdinalIgnoreCase));
                            }

                            var hasTeamAccess = false;
                            if (allowedTeams.Length > 0)
                            {
                                using var teamRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/teams?per_page=100");
                                teamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                                teamRequest.Headers.UserAgent.ParseAdd("SimpleL7Proxy-CompanionApp");
                                using var teamResponse = await context.Backchannel.SendAsync(
                                    teamRequest,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    context.HttpContext.RequestAborted);
                                teamResponse.EnsureSuccessStatusCode();
                                using var teams = JsonDocument.Parse(await teamResponse.Content.ReadAsStringAsync());
                                hasTeamAccess = teams.RootElement.EnumerateArray().Any(team =>
                                {
                                    var organization = team.GetProperty("organization").GetProperty("login").GetString();
                                    var teamName = team.GetProperty("slug").GetString();
                                    return allowedTeams.Contains($"{organization}/{teamName}", StringComparer.OrdinalIgnoreCase);
                                });
                            }

                            if (allowedUsers.Length + allowedOrganizations.Length + allowedTeams.Length > 0
                                && !isAllowedUser && !hasOrganizationAccess && !hasTeamAccess)
                            {
                                context.Fail("The GitHub account is not authorized for Companion App.");
                                return;
                            }

                            context.HttpContext.RequestServices
                                .GetRequiredService<ILoggerFactory>()
                                .CreateLogger("CompanionApp.Authentication")
                                .LogInformation("User authenticated: {User} via GitHub.", login);
                        };
                        options.Events.OnRemoteFailure = context =>
                        {
                            context.HandleResponse();
                            context.Response.Redirect(unauthorizedRedirectUrl);
                            return Task.CompletedTask;
                        };
                    });
                    break;
                case "google":
                    if (string.IsNullOrWhiteSpace(authenticationSection["Google:ClientId"])
                        || string.IsNullOrWhiteSpace(authenticationSection["Google:ClientSecret"]))
                    {
                        throw new InvalidOperationException("Google authentication requires a client ID and client secret.");
                    }

                    authenticationBuilder.AddOpenIdConnect("Google", options =>
                    {
                        options.Authority = authenticationSection["Google:Authority"] ?? "https://accounts.google.com";
                        options.ClientId = authenticationSection["Google:ClientId"] ?? string.Empty;
                        options.ClientSecret = authenticationSection["Google:ClientSecret"] ?? string.Empty;
                        options.CallbackPath = authenticationSection["Google:CallbackPath"] ?? "/signin-google";
                        options.ResponseType = "code";
                        options.SignInScheme = cookieScheme;
                        options.SaveTokens = false;
                        options.GetClaimsFromUserInfoEndpoint = true;
                        options.Scope.Clear();
                        foreach (var scope in authenticationSection.GetSection("Google:Scopes").Get<string[]>() ?? [])
                        {
                            options.Scope.Add(scope);
                        }

                        options.Events.OnTokenValidated = context =>
                        {
                            var email = context.Principal?.FindFirstValue(ClaimTypes.Email)
                                ?? context.Principal?.FindFirstValue("email");
                            var domain = context.Principal?.FindFirstValue("hd")
                                ?? email?.Split('@').LastOrDefault();
                            var allowedUsers = authenticationSection.GetSection("Google:AllowedUsers").Get<string[]>() ?? [];
                            var allowedDomains = authenticationSection.GetSection("Google:AllowedDomains").Get<string[]>() ?? [];
                            var userAllowed = allowedUsers.Contains(email, StringComparer.OrdinalIgnoreCase);
                            var domainAllowed = allowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
                            if (allowedUsers.Length + allowedDomains.Length > 0 && !userAllowed && !domainAllowed)
                            {
                                context.Fail("The Google account is not authorized for Companion App.");
                                return Task.CompletedTask;
                            }

                            context.HttpContext.RequestServices
                                .GetRequiredService<ILoggerFactory>()
                                .CreateLogger("CompanionApp.Authentication")
                                .LogInformation("User authenticated: {User} via Google.", email ?? "unknown");
                            return Task.CompletedTask;
                        };
                        options.Events.OnRemoteFailure = context =>
                        {
                            context.HandleResponse();
                            context.Response.Redirect(unauthorizedRedirectUrl);
                            return Task.CompletedTask;
                        };
                    });
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported CompanionApp authentication provider: {authenticationProvider}");
            }

            var requiredClaims = authenticationSection.GetSection("OAuth2:RequiredClaims")
                .Get<Dictionary<string, string>>() ?? new(StringComparer.OrdinalIgnoreCase);
            builder.Services.AddAuthorization(options => options.AddPolicy("CompanionApp", policy =>
            {
                policy.RequireAuthenticatedUser();
                if (authenticationProvider.Equals("OAuth2", StringComparison.OrdinalIgnoreCase))
                {
                    policy.RequireAssertion(context => requiredClaims.All(requiredClaim =>
                        string.Equals(context.User.FindFirst(requiredClaim.Key)?.Value,
                            requiredClaim.Value,
                            StringComparison.OrdinalIgnoreCase)));
                }
            }));
            builder.Services.AddCascadingAuthenticationState();
        }

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
        builder.Services.AddSingleton(new DefaultAzureCredential(new DefaultAzureCredentialOptions()));
        builder.Services.AddSingleton<AppConfigurationScaffoldService>();
        builder.Services.AddSingleton<ImageSyncService>();
        builder.Services.AddSingleton(serviceProvider => new Azure.ResourceManager.ArmClient(
            serviceProvider.GetRequiredService<DefaultAzureCredential>()));
        builder.Services.AddHttpClient(nameof(ContainerAppManagementService), client => {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {
            AllowAutoRedirect = false
        });
        builder.Services.AddSingleton<ContainerAppManagementService>();
        builder.Services.AddSingleton<ChatHistoryStore>();
        builder.Services.AddSingleton<ChatConversationStore>();
        builder.Services.AddSingleton<EventHubMonitorStore>();

        TokenomicsStartup.ConfigureServices(builder, replayOptions, uiOnly);
        builder.Services.AddSingleton<CompanionApp.Observability.ObservabilityDashboardStore>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<CompanionApp.Observability.ObservabilityDashboardStore>());
        ConfigureOptions(builder);
    }

    private static void ConfigureOptions(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<UserPreferencesService>();
        builder.Services.Configure<CompanionAppOptions>(
            builder.Configuration.GetSection(CompanionAppOptions.SectionName));
        builder.Services.Configure<CompanionAppOptions>(options =>
        {
            options.AppConfigurationRules = builder.Configuration
                .GetSection($"{CompanionAppOptions.UiSectionName}:AppConfigurationRules")
                .Get<List<AppConfigurationSettingRule>>() ?? new();
            options.Hosts = builder.Configuration
                .GetSection($"{CompanionAppOptions.UiSectionName}:Hosts")
                .Get<AppConfigHostSettings>() ?? new();
            options.Hosts.FieldChoices["processor"] = StreamProcessorFactory.ProcessorNames.ToArray();
        });
        builder.Services.Configure<EventHubMonitorOptions>(
            builder.Configuration.GetSection(EventHubMonitorOptions.SectionName));
    }

    private static void StartupHttpPipeline(WebApplication app)
    {
        app.UseForwardedHeaders();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute(
            "/not-found",
            createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();
        app.UseAntiforgery();

        var authenticationSection = app.Configuration.GetSection("CompanionApp:Authentication");
        var authenticationEnabled = authenticationSection.GetValue<bool>("Enabled");
        var configuredAuthenticationProvider = authenticationSection["Provider"] ?? "None";
        var authenticationProvider = configuredAuthenticationProvider.ToLowerInvariant() switch
        {
            "none" => "None",
            "oauth2" => "OAuth2",
            "github" => "GitHub",
            "google" => "Google",
            _ => configuredAuthenticationProvider
        };
        var loginUrl = authenticationSection["LoginUrl"] ?? "/login";
        var logoutUrl = authenticationSection["LogoutUrl"] ?? "/logout";
        var unauthorizedRedirectUrl = authenticationSection["UnauthorizedRedirectUrl"] ?? "/access-denied";
        var postLogoutRedirectUrl = authenticationSection["PostLogoutRedirectUrl"] ?? "/";
        if (authenticationEnabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet(loginUrl, async (HttpContext context, string? returnUrl) =>
            {
                if (authenticationProvider.Equals("OAuth2", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Unauthorized();
                }

                var redirectUrl = !string.IsNullOrWhiteSpace(returnUrl)
                    && returnUrl.StartsWith('/')
                    && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                    ? returnUrl
                    : "/";
                await context.ChallengeAsync(authenticationProvider, new AuthenticationProperties
                {
                    RedirectUri = redirectUrl
                });
                return Results.Empty;
            }).AllowAnonymous();
            app.MapGet(logoutUrl, async (HttpContext context) =>
            {
                await context.SignOutAsync("CompanionAppCookie", new AuthenticationProperties
                {
                    RedirectUri = postLogoutRedirectUrl
                });
                return Results.Empty;
            }).RequireAuthorization("CompanionApp");
            app.MapGet(unauthorizedRedirectUrl, () =>
                Results.Text("Access denied", "text/plain", statusCode: StatusCodes.Status403Forbidden))
                .AllowAnonymous();
        }

        app.MapStaticAssets().AllowAnonymous();
        var components = app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        if (authenticationEnabled)
        {
            components.RequireAuthorization("CompanionApp");
        }
    }

    internal static TokenomicsReplayOptions ParseCommandLineArguments(string[] args)
    {
        var applicationArgs = new List<string>();
        string? replayFileName = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--run", StringComparison.OrdinalIgnoreCase))
            {
                if (args[index].StartsWith("--run=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Use --run events <filename>.");
                }

                applicationArgs.Add(args[index]);
                continue;
            }

            if (replayFileName is not null
                || index + 2 >= args.Length
                || !args[index + 1].Equals("events", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(args[index + 2])
                || args[index + 2].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Replay requires exactly one --run events <filename>.");
            }

            replayFileName = args[index + 2];
            index += 2;
        }

        return TokenomicsReplayOptions.Load(applicationArgs.ToArray(), replayFileName);
    }
}