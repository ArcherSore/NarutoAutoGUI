using System.Text.RegularExpressions;

namespace Protocol;

// A profile folder is usually named after the Windows account, which stays out of logs and telemetry. JSON doubles
// the backslashes in a path, and a longer sibling such as C:\Users\name2 is a different folder.
public static class UserProfileMask
{
    public const string Replacement = "%USERPROFILE%";
    private static readonly Regex? CurrentPattern =
        CreatePattern(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Apply(string text) => Apply(text, CurrentPattern);

    public static string Apply(string text, string profile) => Apply(text, CreatePattern(profile));

    private static string Apply(string text, Regex? pattern) => pattern?.Replace(text, Replacement) ?? text;

    private static Regex? CreatePattern(string profile)
    {
        if (profile.Length == 0) {
            return null;
        }

        var segments = profile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
        return new Regex(string.Join(@"[\\/]+", segments) + @"(?![\p{L}\p{N}_-]|\.[\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
