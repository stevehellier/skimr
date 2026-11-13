namespace Skimr.Domain;

/// <summary>Admin-changeable runtime setting that overrides a config default.</summary>
public sealed class AppSetting
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
