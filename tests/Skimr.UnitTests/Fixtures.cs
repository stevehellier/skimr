namespace Skimr.UnitTests;

internal static class Fixtures
{
    public static byte[] Feed(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "feeds", name));
}
