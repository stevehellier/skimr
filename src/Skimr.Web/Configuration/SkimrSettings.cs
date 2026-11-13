using System.Net;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Identity;

namespace Skimr.Web.Configuration;

internal enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>
/// All settings, validated before the host is built. Read by hand, not with the binder,
/// so every bad value is reported at once with the key name and the app exits cleanly.
/// </summary>
internal sealed class SkimrSettings
{
    public required DatabaseProvider Provider { get; init; }

    public required string ConnectionString { get; init; }

    public required bool AutoMigrate { get; init; }

    /// <summary>Absolute path. Holds the SQLite database, logs, icons and cache.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Empty, or a path starting with "/" and no trailing slash, e.g. "/skimr".</summary>
    public required string BasePath { get; init; }

    public required IReadOnlyList<IPAddress> KnownProxies { get; init; }

    public required IReadOnlyList<IPNetwork> KnownNetworks { get; init; }

    public required FetchOptions Fetch { get; init; }

    public required ContentOptions Content { get; init; }

    /// <summary>Used until an admin picks a mode in the setup wizard or admin page.</summary>
    public required RegistrationMode DefaultRegistrationMode { get; init; }

    /// <summary>POSTs per minute per IP to setup, login and register.</summary>
    public required int LoginAttemptsPerMinute { get; init; }

    /// <summary>API requests per minute per user.</summary>
    public required int ApiRequestsPerMinute { get; init; }

    public static SkimrSettings Load(IConfiguration config, string contentRoot)
    {
        var errors = new List<string>();
        var read = new Reader(config, errors);

        var providerText = config["Database:Provider"] ?? nameof(DatabaseProvider.Sqlite);
        if (!Enum.TryParse<DatabaseProvider>(providerText, ignoreCase: true, out var provider)
            || !Enum.IsDefined(provider))
        {
            errors.Add($"Database:Provider must be 'Sqlite' or 'Postgres' (got '{providerText}').");
        }

        var autoMigrate = read.Bool("Database:AutoMigrate", true);

        var dataDirectory = Path.GetFullPath(Path.Combine(contentRoot, config["Storage:DataDirectory"] ?? "data"));

        var connectionString = config["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (provider == DatabaseProvider.Postgres)
            {
                errors.Add("Database:ConnectionString is required when Database:Provider is 'Postgres'.");
            }

            connectionString = $"Data Source={Path.Combine(dataDirectory, "skimr.db")}";
        }

        var basePath = (config["Server:BasePath"] ?? "").Trim().TrimEnd('/');
        if (basePath.Length > 0 && (!basePath.StartsWith('/') || basePath.IndexOfAny(['?', '#', ' ']) >= 0))
        {
            errors.Add($"Server:BasePath must start with '/' and contain no '?', '#' or spaces (got '{basePath}').");
        }

        var proxies = read.List("Server:KnownProxies", (string v, out IPAddress? ip) => IPAddress.TryParse(v, out ip), "an IP address");
        var networks = read.List("Server:KnownNetworks", (string v, out IPNetwork n) => IPNetwork.TryParse(v, out n), "a CIDR range like 172.16.0.0/12");

        Uri? publicUrl = null;
        if (config["Server:PublicUrl"] is { Length: > 0 } publicText)
        {
            if (!Uri.TryCreate(publicText.TrimEnd('/'), UriKind.Absolute, out publicUrl)
                || publicUrl.Scheme is not ("http" or "https") || publicUrl.Query.Length > 0)
            {
                errors.Add($"Server:PublicUrl must be an absolute http(s) URL with no query string (got '{publicText}').");
            }
        }

        var fetch = new FetchOptions
        {
            Enabled = read.Bool("Fetch:Enabled", true),
            Concurrency = read.Int("Fetch:Concurrency", 8, 1, 64),
            DefaultIntervalMinutes = read.Int("Fetch:DefaultIntervalMinutes", 30, FetchOptions.MinIntervalMinutes, 24 * 60),
            Timeout = TimeSpan.FromSeconds(read.Int("Fetch:TimeoutSeconds", 30, 5, 300)),
            MaxResponseBytes = read.Int("Fetch:MaxResponseMegabytes", 10, 1, 100) * 1024L * 1024,
            MaxRedirects = read.Int("Fetch:MaxRedirects", 5, 0, 10),
            UserAgent = config["Fetch:UserAgent"] is { Length: > 0 } ua ? ua : new FetchOptions().UserAgent,
            AllowPrivateNetworks = read.Bool("Fetch:AllowPrivateNetworkFeeds", false),
            PublicUrl = publicUrl,
        };

        var embedHosts = read.List("Content:AllowedEmbedHosts", (string v, out string? host) =>
        {
            host = v.ToLowerInvariant();
            return Uri.CheckHostName(v) == UriHostNameType.Dns;
        }, "a host name like www.youtube.com");
        var content = config.GetSection("Content:AllowedEmbedHosts").Exists()
            ? new ContentOptions { AllowedEmbedHosts = embedHosts }
            : new ContentOptions();

        var registrationText = config["Registration:DefaultMode"] ?? nameof(RegistrationMode.InviteOnly);
        if (!Enum.TryParse<RegistrationMode>(registrationText, ignoreCase: true, out var registration) || !Enum.IsDefined(registration))
        {
            errors.Add($"Registration:DefaultMode must be 'Open', 'InviteOnly' or 'Closed' (got '{registrationText}').");
        }

        var loginPerMinute = read.Int("Security:LoginAttemptsPerMinute", 10, 1, 10_000);
        var apiPerMinute = read.Int("Security:ApiRequestsPerMinute", 600, 10, 1_000_000);

        if (errors.Count == 0)
        {
            EnsureWritable(dataDirectory, errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidConfigurationException(errors);
        }

        return new SkimrSettings
        {
            Provider = provider,
            ConnectionString = connectionString,
            AutoMigrate = autoMigrate,
            DataDirectory = dataDirectory,
            BasePath = basePath,
            KnownProxies = proxies,
            KnownNetworks = networks,
            Fetch = fetch,
            Content = content,
            DefaultRegistrationMode = registration,
            LoginAttemptsPerMinute = loginPerMinute,
            ApiRequestsPerMinute = apiPerMinute,
        };
    }

    private static void EnsureWritable(string dataDirectory, List<string> errors)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var probe = Path.Combine(dataDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Storage:DataDirectory '{dataDirectory}' is not writable: {ex.Message}");
        }
    }

    private delegate bool TryParse<T>(string value, out T result);

    private sealed class Reader(IConfiguration config, List<string> errors)
    {
        public bool Bool(string key, bool fallback)
        {
            var text = config[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (bool.TryParse(text, out var value))
            {
                return value;
            }

            errors.Add($"{key} must be 'true' or 'false' (got '{text}').");
            return fallback;
        }

        public int Int(string key, int fallback, int min, int max)
        {
            var text = config[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (int.TryParse(text, out var value) && value >= min && value <= max)
            {
                return value;
            }

            errors.Add($"{key} must be a whole number from {min} to {max} (got '{text}').");
            return fallback;
        }

        /// <summary>Accepts either a comma-separated string or a config array (Key:0, Key:1 ...).</summary>
        public List<T> List<T>(string key, TryParse<T?> parse, string expected)
        {
            var section = config.GetSection(key);
            var values = section.Value is { } single
                ? single.Split(',')
                : section.GetChildren().Select(c => c.Value ?? "");

            var result = new List<T>();
            foreach (var value in values.Select(v => v.Trim()).Where(v => v.Length > 0))
            {
                if (parse(value, out var parsed) && parsed is not null)
                {
                    result.Add(parsed);
                }
                else
                {
                    errors.Add($"{key} contains '{value}', which is not {expected}.");
                }
            }

            return result;
        }
    }
}

internal sealed class InvalidConfigurationException(IReadOnlyList<string> errors)
    : Exception("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
