using Skimr.Infrastructure.Fetching;

namespace Skimr.UnitTests.Fetching;

public sealed class RefreshScheduleTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Thirty = TimeSpan.FromMinutes(30);

    private static TimeSpan Delay(int failures, TimeSpan? maxAge = null, TimeSpan? retryAfter = null, double jitter = 0) =>
        RefreshSchedule.Next(Now, Thirty, failures, maxAge, retryAfter, jitter) - Now;

    [Fact]
    public void Success_waits_one_interval()
    {
        Delay(0).Should().Be(Thirty);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(5, 960)]
    public void Failures_back_off_exponentially(int failures, int expectedMinutes)
    {
        Delay(failures).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void Backoff_is_capped_at_a_day()
    {
        Delay(8).Should().Be(TimeSpan.FromHours(24));
        Delay(1000).Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Cache_control_max_age_can_only_lengthen_the_wait()
    {
        Delay(0, maxAge: TimeSpan.FromHours(2)).Should().Be(TimeSpan.FromHours(2));
        Delay(0, maxAge: TimeSpan.FromMinutes(1)).Should().Be(Thirty);
        Delay(0, maxAge: TimeSpan.FromDays(30)).Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Retry_after_is_respected_up_to_the_cap()
    {
        Delay(1, retryAfter: TimeSpan.FromHours(3)).Should().Be(TimeSpan.FromHours(3));
        Delay(1, retryAfter: TimeSpan.FromSeconds(10)).Should().Be(TimeSpan.FromMinutes(60));
        Delay(1, retryAfter: TimeSpan.FromDays(3)).Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Jitter_spreads_by_ten_percent()
    {
        Delay(0, jitter: 1).Should().Be(TimeSpan.FromMinutes(33));
        Delay(0, jitter: -1).Should().Be(TimeSpan.FromMinutes(27));
    }

    [Fact]
    public void Effective_interval_is_the_shortest_subscriber_interval_with_a_floor()
    {
        RefreshSchedule.EffectiveInterval([60, null, 15], 30).Should().Be(TimeSpan.FromMinutes(15));
        RefreshSchedule.EffectiveInterval([null], 30).Should().Be(Thirty);
        RefreshSchedule.EffectiveInterval([1], 30).Should().Be(TimeSpan.FromMinutes(5));
        RefreshSchedule.EffectiveInterval([], 30).Should().Be(Thirty);
    }
}
