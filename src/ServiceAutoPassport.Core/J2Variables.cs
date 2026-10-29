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

    public static string? ExtractAssignedVariable(string template, string key)
    {
        ArgumentNullException.ThrowIfNull(template);
        var lines = template.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon < 1 || !line[..colon].Trim().Trim('\'', '"').Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0 && index + 1 < lines.Length) value = lines[index + 1].Trim();
            var match = SinglePlaceholderRegex().Match(value);
            if (match.Success) return match.Groups["variable"].Value;
        }
        return null;
    }

    [GeneratedRegex(@"^(?<quote>['""]?)[ \t]*\{\{\s*(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}[ \t]*\k<quote>[ \t]*(?:#[^\r\n]*)?$")]
    private static partial Regex SinglePlaceholderRegex();

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}
