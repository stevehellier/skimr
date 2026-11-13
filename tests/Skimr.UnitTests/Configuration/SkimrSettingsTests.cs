using System.Net;
using Microsoft.Extensions.Configuration;
using Skimr.Web.Configuration;

namespace Skimr.UnitTests.Configuration;

public sealed class SkimrSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skimr-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SkimrSettings Load(params (string Key, string Value)[] values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        return SkimrSettings.Load(config, _root);
    }

    [Fact]
    public void Defaults_to_sqlite_in_data_directory_under_content_root()
    {
        var settings = Load();

        settings.Provider.Should().Be(DatabaseProvider.Sqlite);
        settings.DataDirectory.Should().Be(Path.Combine(_root, "data"));
        settings.ConnectionString.Should().Be($"Data Source={Path.Combine(_root, "data", "skimr.db")}");
        settings.AutoMigrate.Should().BeTrue();
        settings.BasePath.Should().BeEmpty();
        Directory.Exists(settings.DataDirectory).Should().BeTrue();
    }

    [Fact]
    public void Postgres_without_connection_string_fails()
    {
        var act = () => Load(("Database:Provider", "postgres"));

        act.Should().Throw<InvalidConfigurationException>()
            .Which.Errors.Should().ContainSingle(e => e.Contains("Database:ConnectionString"));
    }

    [Fact]
    public void Reports_every_bad_value_at_once()
    {
        var act = () => Load(
            ("Database:Provider", "mysql"),
            ("Database:AutoMigrate", "sometimes"),
            ("Server:BasePath", "skimr"),
            ("Server:KnownProxies", "10.0.0.1, not-an-ip"),
            ("Server:KnownNetworks", "10.0.0.0/33"));

        act.Should().Throw<InvalidConfigurationException>()
            .Which.Errors.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("/skimr", "/skimr")]
    [InlineData("/skimr/", "/skimr")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public void Normalises_base_path(string input, string expected)
    {
        Load(("Server:BasePath", input)).BasePath.Should().Be(expected);
    }

    [Fact]
    public void Reads_proxies_from_comma_list_or_array()
    {
        Load(("Server:KnownProxies", "10.0.0.1,10.0.0.2")).KnownProxies
            .Should().Equal(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"));

        Load(("Server:KnownProxies:0", "10.0.0.3")).KnownProxies
            .Should().Equal(IPAddress.Parse("10.0.0.3"));

        Load(("Server:KnownNetworks", "172.16.0.0/12")).KnownNetworks
            .Should().Equal(IPNetwork.Parse("172.16.0.0/12"));
    }

    [Fact]
    public void Unwritable_data_directory_fails()
    {
        // A file where the directory should be makes CreateDirectory fail on every OS.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "blocked"), "");

        var act = () => Load(("Storage:DataDirectory", "blocked"));

        act.Should().Throw<InvalidConfigurationException>()
            .Which.Errors.Should().ContainSingle(e => e.Contains("not writable"));
    }
}
