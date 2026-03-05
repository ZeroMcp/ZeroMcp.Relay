using System.Text.RegularExpressions;

namespace ZeroMcp.Relay.Config;

public static class GlobMatcher
{
    public static bool IsMatch(string candidate, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return Regex.IsMatch(candidate, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsIncluded(string candidate, IReadOnlyCollection<string> include, IReadOnlyCollection<string> exclude)
    {
        var included = include.Count == 0 || include.Any(pattern => IsMatch(candidate, pattern));
        if (!included)
        {
            return false;
        }

        return !exclude.Any(pattern => IsMatch(candidate, pattern));
    }
}
