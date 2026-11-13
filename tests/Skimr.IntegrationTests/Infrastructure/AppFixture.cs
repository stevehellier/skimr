using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Skimr.IntegrationTests.Infrastructure;

/// <summary>Boots the app against one database provider. Each provider gets its own subclass.</summary>
public abstract class AppFixture : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "skimr-tests", Guid.NewGuid().ToString("N"));

    public SkimrFactory Factory { get; private set; } = null!;

    protected abstract ValueTask<IReadOnlyDictionary<string, string>> DatabaseSettingsAsync();

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string>(await DatabaseSettingsAsync())
        {
            ["Storage:DataDirectory"] = _dataDirectory,
        };
        Factory = new SkimrFactory(settings);
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

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

public sealed class SqliteAppFixture : AppFixture
{
    protected override ValueTask<IReadOnlyDictionary<string, string>> DatabaseSettingsAsync() =>
        ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["Database:Provider"] = "Sqlite",
        });
}

public sealed class PostgresAppFixture : AppFixture
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override async ValueTask<IReadOnlyDictionary<string, string>> DatabaseSettingsAsync()
    {
        await _postgres.StartAsync();
        return new Dictionary<string, string>
        {
            ["Database:Provider"] = "Postgres",
            ["Database:ConnectionString"] = _postgres.GetConnectionString(),
        };
    }

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
