namespace Skimr.Infrastructure.Ai;

public sealed record AiArticle(string Title, string Text);

/// <summary>The configured model, behind an interface so tests can use a fake provider (PLAN Phase 7).</summary>
public interface ISummariser
{
    /// <summary>A short plain-text summary.</summary>
    /// <exception cref="AiException">The provider failed or declined.</exception>
    Task<string> SummariseAsync(AiArticle article, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="articles"/> match the user's rule, by index.</summary>
    /// <exception cref="AiException">The provider failed, declined, or answered in an unusable form.</exception>
    Task<IReadOnlySet<int>> MatchAsync(string rule, IReadOnlyList<AiArticle> articles, CancellationToken cancellationToken);
}

/// <summary>An AI call that didn't produce a usable answer. The message is shown to users.</summary>
/// <param name="Transient">True for rate limits, overload and network trouble: worth retrying later.</param>
public sealed class AiException(string message, bool transient = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}
