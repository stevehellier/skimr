namespace Skimr.Infrastructure.Reading;

public sealed record RetentionOptions
{
    /// <summary>Runs the background purge. Tests turn it off and call the purge directly.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Read articles published longer ago than this are removed (never saved or boarded ones).</summary>
    public int Days { get; init; } = 60;
}
