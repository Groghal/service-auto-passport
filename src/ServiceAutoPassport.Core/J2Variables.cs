using System.Text.RegularExpressions;

namespace ServiceAutoPassport.Core;

public static partial class J2Variables
{
    /// <summary>Uses the simple variable syntax supported by j2magicwand.</summary>
    public static IReadOnlyList<string> Extract(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return PlaceholderRegex().Matches(template)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}
