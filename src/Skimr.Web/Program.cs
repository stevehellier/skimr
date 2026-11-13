using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Skimr.Infrastructure;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Identity;
using Skimr.Infrastructure.Persistence;
using Skimr.Infrastructure.Postgres;
using Skimr.Infrastructure.Reading;
using Skimr.Infrastructure.Sqlite;
using Skimr.Web.Api;
using Skimr.Web.Auth;
using Skimr.Web.Components;
using Skimr.Web.Configuration;
using Skimr.Web.Hosting;

if (args is ["--healthcheck", ..])
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Order: appsettings -> environment (SKIMR__Section__Key) -> command line.
builder.Configuration.AddEnvironmentVariables("SKIMR__");
builder.Configuration.AddCommandLine(args);

SkimrSettings settings;
try
{
    settings = SkimrSettings.Load(builder.Configuration, builder.Environment.ContentRootPath);
}
catch (InvalidConfigurationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

builder.Services.AddSingleton(settings);

// Keys live in the data directory so cookies survive restarts and container rebuilds.
builder.Services.AddDataProtection()
    .SetApplicationName("Skimr")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "keys")));

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(settings.DataDirectory, "logs", "skimr-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14),
    preserveStaticLogger: true);

// The factory serves interactive UI services (a circuit outlives any one operation); it also
// registers a scoped SkimrDbContext for Identity, the refresher and request-scoped code.
builder.Services.AddDbContextFactory<SkimrDbContext>(options => _ = settings.Provider switch
{
    DatabaseProvider.Sqlite => options.UseSkimrSqlite(settings.ConnectionString),
    DatabaseProvider.Postgres => options.UseSkimrPostgres(settings.ConnectionString),
    _ => throw new InvalidOperationException($"Unknown provider {settings.Provider}"),
});

builder.Services.AddSkimrInfrastructure(settings.Fetch, settings.Content, settings.Retention);
builder.Services.AddSingleton(new StorageOptions(settings.DataDirectory));
builder.Services.AddSingleton(ImageProxyKey.LoadOrCreate(settings.DataDirectory));
builder.Services.AddSingleton<IArticleSearch>(settings.Provider == DatabaseProvider.Postgres ? new PostgresArticleSearch() : new SqliteArticleSearch());
builder.Services.AddSkimrAuth(settings);
builder.Services.AddApiDocs();
// API enums travel as names ("Oldest"), which the OpenAPI document then lists.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in settings.KnownProxies)
    {
        options.KnownProxies.Add(proxy);
    }

    foreach (var network in settings.KnownNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

try
{
    await DatabaseStartup.InitialiseAsync(app.Services, settings, app.Lifetime.ApplicationStopping);
    await app.Services.GetRequiredService<SetupService>().InitialiseAsync(app.Lifetime.ApplicationStopping);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Database initialisation failed ({Provider}). Check Database:ConnectionString.", settings.Provider);
    return 1;
}

app.UseForwardedHeaders();
if (settings.BasePath.Length > 0)
{
    app.UsePathBase(settings.BasePath);
}

app.UseRouting();
app.UseSecurityHeaders(settings.Content);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);

// Order matters: the setup redirect needs the endpoint and user; antiforgery must follow auth.
app.UseAuthentication();
app.UseSetupRedirect();
app.UseAuthorization();
// After authorization: the API's bearer token is only authenticated by its policy, and the
// limiter partitions API requests by that user (PLAN D38).
app.UseRateLimiter();
app.UseAntiforgery();

// Anonymous by design: Docker's HEALTHCHECK, hub callbacks (HMAC-signed) and static files.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().WithMetadata(new SkipStatusCodePagesAttribute());
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous().WithMetadata(new SkipStatusCodePagesAttribute());
app.MapWebSub(settings.Fetch);
app.MapStaticAssets().AllowAnonymous();

app.MapAccountEndpoints();
app.MapRestApi();
app.MapGoogleReaderApi();
app.MapFeverApi();
app.MapApiDocs();
app.MapReaderEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

public partial class Program;
