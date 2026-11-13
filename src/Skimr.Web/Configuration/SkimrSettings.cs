using System.Net;

namespace Skimr.Web.Configuration;

internal enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>
/// Settings needed before the host is built. Read by hand, not with the binder,
/// so every bad value is reported at once with the key name.
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

    public static SkimrSettings Load(IConfiguration config, string contentRoot)
    {
        var errors = new List<string>();

        var providerText = config["Database:Provider"] ?? nameof(DatabaseProvider.Sqlite);
        if (!Enum.TryParse<DatabaseProvider>(providerText, ignoreCase: true, out var provider)
            || !Enum.IsDefined(provider))
        {
            errors.Add($"Database:Provider must be 'Sqlite' or 'Postgres' (got '{providerText}').");
        }

        var autoMigrateText = config["Database:AutoMigrate"];
        var autoMigrate = true;
        if (autoMigrateText is not null && !bool.TryParse(autoMigrateText, out autoMigrate))
        {
            errors.Add($"Database:AutoMigrate must be 'true' or 'false' (got '{autoMigrateText}').");
        }

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

        var proxies = new List<IPAddress>();
        foreach (var value in ReadList(config, "Server:KnownProxies"))
        {
            if (IPAddress.TryParse(value, out var ip))
            {
                proxies.Add(ip);
            }
            else
            {
                errors.Add($"Server:KnownProxies contains '{value}', which is not an IP address.");
            }
        }

        var networks = new List<IPNetwork>();
        foreach (var value in ReadList(config, "Server:KnownNetworks"))
        {
            if (IPNetwork.TryParse(value, out var network))
            {
                networks.Add(network);
            }
            else
            {
                errors.Add($"Server:KnownNetworks contains '{value}', which is not a CIDR range like 172.16.0.0/12.");
            }
        }

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
        };
    }

    /// <summary>Accepts either a comma-separated string or a config array (Key:0, Key:1 ...).</summary>
    private static IEnumerable<string> ReadList(IConfiguration config, string key)
    {
        var section = config.GetSection(key);
        var values = section.Value is { } single
            ? single.Split(',')
            : section.GetChildren().Select(c => c.Value ?? "");
        return values.Select(v => v.Trim()).Where(v => v.Length > 0);
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
}

internal sealed class InvalidConfigurationException(IReadOnlyList<string> errors)
    : Exception("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
