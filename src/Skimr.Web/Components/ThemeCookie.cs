namespace Skimr.Web.Components;

/// <summary>
/// The theme is per device (PLAN D43): a cookie set by reader.js and read here so the server
/// renders the right <c>data-theme</c> on the first byte, with no flash of the wrong theme.
/// </summary>
internal static class ThemeCookie
{
    public const string Name = "skimr.theme";

    public static string Read(HttpContext? context) =>
        context?.Request.Cookies[Name] is "light" or "dark" ? context.Request.Cookies[Name]! : "system";
}
