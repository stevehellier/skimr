namespace Skimr.Infrastructure.Ai;

public enum AiProvider
{
    None,
    Anthropic,
    OpenAICompatible,
}

/// <summary>Admin-configured AI provider (PLAN D12/D13). Off unless <see cref="Provider"/> is set.</summary>
public sealed record AiOptions
{
    public static readonly Uri AnthropicDefaultBaseUrl = new("https://api.anthropic.com/");

    public AiProvider Provider { get; init; } = AiProvider.None;

    /// <summary>Anthropic: the API root. OpenAI-compatible: the root that has /chat/completions (e.g. http://localhost:11434/v1/).</summary>
    public Uri? BaseUrl { get; init; }

    public string Model { get; init; } = "";

    public string? ApiKey { get; init; }

    public int DailyCallsPerUser { get; init; } = 500;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Articles per AI-feed classification call.</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>How often the AI-feed worker looks for new articles.</summary>
    public TimeSpan MatchInterval { get; init; } = TimeSpan.FromMinutes(2);

    public bool Enabled => Provider != AiProvider.None;
}
