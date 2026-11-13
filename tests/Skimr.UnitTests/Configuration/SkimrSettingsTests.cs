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
    public void Ai_is_off_by_default()
    {
        var settings = Load();

        settings.Ai.Enabled.Should().BeFalse();
        settings.Ai.DailyCallsPerUser.Should().Be(500);
    }

    [Fact]
    public void Anthropic_defaults_its_base_url_and_needs_a_model_and_key()
    {
        Load(("Ai:Provider", "anthropic"), ("Ai:Model", "claude-opus-5-5"), ("Ai:ApiKey", "k")).Ai.BaseUrl
            .Should().Be(new Uri("https://api.anthropic.com/"));

        var act = () => Load(("Ai:Provider", "Anthropic"));

        act.Should().Throw<InvalidConfigurationException>()
            .Which.Errors.Should().Satisfy(e => e.Contains("Ai:Model"), e => e.Contains("Ai:ApiKey"));
    }

    [Theory]
    [InlineData("OpenAICompatible", "", "Ai:BaseUrl is required")]
    [InlineData("OpenAICompatible", "ftp://host/v1", "Ai:BaseUrl must be")]
    [InlineData("OpenAICompatible", "http://user:pass@host/v1", "Ai:BaseUrl must be")]
    [InlineData("Gemini", "", "Ai:Provider must be")]
    public void Bad_ai_settings_fail_with_the_key_name(string provider, string baseUrl, string error)
    {
        var act = () => Load(("Ai:Provider", provider), ("Ai:Model", "m"), ("Ai:BaseUrl", baseUrl));

        act.Should().Throw<InvalidConfigurationException>().Which.Errors.Should().ContainSingle(e => e.Contains(error));
    }

    [Fact]
    public void Open_ai_compatible_needs_no_key_and_keeps_its_path()
    {
        var ai = Load(("Ai:Provider", "OpenAICompatible"), ("Ai:Model", "llama3"), ("Ai:BaseUrl", "http://localhost:11434/v1")).Ai;

        ai.BaseUrl.Should().Be(new Uri("http://localhost:11434/v1/"));
        ai.ApiKey.Should().BeNull();
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
    public void Fetch_and_content_defaults()
    {
        var settings = Load();

        settings.Fetch.Enabled.Should().BeTrue();
        settings.Fetch.Concurrency.Should().Be(8);
        settings.Fetch.DefaultIntervalMinutes.Should().Be(30);
        settings.Fetch.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        settings.Fetch.MaxResponseBytes.Should().Be(10 * 1024 * 1024);
        settings.Fetch.MaxRedirects.Should().Be(5);
        settings.Fetch.AllowPrivateNetworks.Should().BeFalse();
        settings.Fetch.PublicUrl.Should().BeNull();
        settings.Content.AllowedEmbedHosts.Should().Contain("www.youtube-nocookie.com");
    }

    [Fact]
    public void Fetch_and_content_values_are_read()
    {
        var settings = Load(
            ("Fetch:Concurrency", "2"),
            ("Fetch:DefaultIntervalMinutes", "60"),
            ("Fetch:AllowPrivateNetworkFeeds", "true"),
            ("Server:PublicUrl", "https://reader.example.com/skimr/"),
            ("Content:AllowedEmbedHosts", "player.vimeo.com"));

        settings.Fetch.Concurrency.Should().Be(2);
        settings.Fetch.DefaultIntervalMinutes.Should().Be(60);
        settings.Fetch.AllowPrivateNetworks.Should().BeTrue();
        settings.Fetch.PublicUrl.Should().Be(new Uri("https://reader.example.com/skimr"));
        settings.Content.AllowedEmbedHosts.Should().Equal("player.vimeo.com");
    }

    [Fact]
    public void Empty_embed_list_disables_embeds()
    {
        Load(("Content:AllowedEmbedHosts", "")).Content.AllowedEmbedHosts.Should().BeEmpty();
    }

    [Fact]
    public void Bad_fetch_values_are_all_reported()
    {
        var act = () => Load(
            ("Fetch:Concurrency", "0"),
            ("Fetch:DefaultIntervalMinutes", "4"),
            ("Fetch:TimeoutSeconds", "abc"),
            ("Fetch:MaxResponseMegabytes", "500"),
            ("Fetch:AllowPrivateNetworkFeeds", "yes"),
            ("Server:PublicUrl", "ftp://x"),
            ("Content:AllowedEmbedHosts", "not a host"));

        act.Should().Throw<InvalidConfigurationException>()
            .Which.Errors.Should().HaveCount(7)
            .And.Contain(e => e.StartsWith("Fetch:DefaultIntervalMinutes must be a whole number from 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Registration_and_rate_limit_defaults_and_values()
    {
        var defaults = Load();
        defaults.DefaultRegistrationMode.Should().Be(Skimr.Infrastructure.Identity.RegistrationMode.InviteOnly);
        defaults.LoginAttemptsPerMinute.Should().Be(10);
        defaults.ApiRequestsPerMinute.Should().Be(600);

        var custom = Load(("Registration:DefaultMode", "closed"), ("Security:LoginAttemptsPerMinute", "3"), ("Security:ApiRequestsPerMinute", "60"));
        custom.DefaultRegistrationMode.Should().Be(Skimr.Infrastructure.Identity.RegistrationMode.Closed);
        custom.LoginAttemptsPerMinute.Should().Be(3);
        custom.ApiRequestsPerMinute.Should().Be(60);
    }

    [Fact]
    public void Bad_registration_and_rate_limit_values_are_reported()
    {
        var act = () => Load(("Registration:DefaultMode", "anyone"), ("Security:LoginAttemptsPerMinute", "0"), ("Security:ApiRequestsPerMinute", "5"));

        act.Should().Throw<InvalidConfigurationException>().Which.Errors.Should().HaveCount(3);
    }

    [Fact]
    public void Retention_and_image_proxy_defaults_values_and_errors()
    {
        var defaults = Load();
        (defaults.Retention.Enabled, defaults.Retention.Days, defaults.Content.ImageProxyEnabled).Should().Be((true, 60, true));

        var custom = Load(("Retention:Enabled", "false"), ("Retention:Days", "30"), ("ImageProxy:Enabled", "false"));
        (custom.Retention.Enabled, custom.Retention.Days, custom.Content.ImageProxyEnabled).Should().Be((false, 30, false));

        var act = () => Load(("Retention:Days", "3"), ("Retention:Enabled", "maybe"), ("ImageProxy:Enabled", "on"));
        act.Should().Throw<InvalidConfigurationException>().Which.Errors.Should().HaveCount(3);
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
