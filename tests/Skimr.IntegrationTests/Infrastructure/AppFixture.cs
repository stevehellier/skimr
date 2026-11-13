using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Skimr.IntegrationTests.Infrastructure;

/// <summary>Boots the app against one database provider. Each provider gets its own subclass.</summary>
public abstract class AppFixture : IAsyncLifetime
{
    private readonly List<string> _dataDirectories = [];

    /// <summary>One shared app per test class. Tests that need an empty database use <see cref="CreateFreshApp"/>.</summary>
    public SkimrFactory Factory { get; private set; } = null!;

    /// <summary>Settings that point at a brand-new, empty database.</summary>
    protected abstract IReadOnlyDictionary<string, string> NewDatabase();

    protected virtual ValueTask StartAsync() => ValueTask.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        await StartAsync();
        Factory = CreateFreshApp();
    }

    /// <summary>A new app with its own empty database and data directory. Dispose it after the test.</summary>
    public SkimrFactory CreateFreshApp(params (string Key, string Value)[] extra)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "skimr-tests", Guid.NewGuid().ToString("N"));
        _dataDirectories.Add(dataDirectory);

        var settings = new Dictionary<string, string>(NewDatabase())
        {
            ["Storage:DataDirectory"] = dataDirectory,
            // Tests drive fetching explicitly; the background scheduler would race them.
            ["Fetch:Enabled"] = "false",
            // Login-heavy tests would otherwise trip each other's rate limit. RateLimitTests lowers it.
            ["Security:LoginAttemptsPerMinute"] = "10000",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return new SkimrFactory(settings);
    }

    public WebApplicationFactory<Program> WithSettings(params (string Key, string Value)[] extra)
    {
        return Factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in extra)
            {
                b.UseSetting(key, value);
            }
        });
    }

    public WebApplicationFactory<Program> WithServices(Action<IServiceCollection> configure) =>
        Factory.WithWebHostBuilder(b => b.ConfigureTestServices(configure));

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var directory in _dataDirectories.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A fresh app a test forgot to dispose can still hold its log file; temp space is fine.
            }
        }

        GC.SuppressFinalize(this);
    }
}

public sealed class SqliteAppFixture : AppFixture
{
    // A new data directory means a new skimr.db file.
    protected override IReadOnlyDictionary<string, string> NewDatabase() =>
        new Dictionary<string, string> { ["Database:Provider"] = "Sqlite" };
}

public sealed class PostgresAppFixture : AppFixture
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override async ValueTask StartAsync() => await _postgres.StartAsync();

    // One container, a new database per app: the app creates it on startup.
    protected override IReadOnlyDictionary<string, string> NewDatabase() =>
        new Dictionary<string, string>
        {
            ["Database:Provider"] = "Postgres",
            ["Database:ConnectionString"] = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            {
                Database = "skimr_" + Guid.NewGuid().ToString("N")[..12],
            }.ConnectionString,
        };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class SkimrFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
