namespace Skimr.Infrastructure.Fetching;

/// <summary>When to fetch a feed next (PLAN D4).</summary>
internal static class RefreshSchedule
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(24);

    private const int MaxBackoffDoublings = 10;
    private const double JitterFraction = 0.1;

    /// <param name="interval">The feed's effective interval: the shortest of its subscribers'.</param>
    /// <param name="consecutiveFailures">Failures including the one just recorded; 0 after a success.</param>
    /// <param name="maxAge">Cache-Control max-age from the last response, if any.</param>
    /// <param name="retryAfter">Retry-After from the last response, if any.</param>
    /// <param name="jitter">A value in [-1, 1]; spreads fetches so feeds added together don't stay in lockstep.</param>
    public static DateTime Next(DateTime now, TimeSpan interval, int consecutiveFailures, TimeSpan? maxAge, TimeSpan? retryAfter, double jitter)
    {
        var delay = consecutiveFailures == 0
            ? Max(interval, maxAge ?? TimeSpan.Zero)
            : interval * Math.Pow(2, Math.Min(consecutiveFailures, MaxBackoffDoublings));

        if (retryAfter is { } wait)
        {
            delay = Max(delay, wait);
        }

        delay = delay > MaxDelay ? MaxDelay : delay;
        return now + delay * (1 + JitterFraction * Math.Clamp(jitter, -1, 1));
    }

    public static TimeSpan EffectiveInterval(IEnumerable<int?> subscriberMinutes, int defaultMinutes)
    {
        var minutes = subscriberMinutes.Select(m => m ?? defaultMinutes).DefaultIfEmpty(defaultMinutes).Min();
        return TimeSpan.FromMinutes(Math.Max(minutes, FetchOptions.MinIntervalMinutes));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
