using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace MyMailBox.Updates;

public static partial class ReleaseVersion
{
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static SemanticVersion? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value[0] is 'v' or 'V') value = value[1..];
        var match = Pattern().Match(value);
        if (!match.Success || match.Groups[4].Value.Split('.').Any(p => p.Length > 1 && p.All(char.IsAsciiDigit) && p[0] == '0')) return null;
        return SemanticVersion.TryParse(value, out var version) ? version : null;
    }
}
