namespace ServiceAutoPassport.Core;

public static class SettingsTableImport
{
    public static IReadOnlyDictionary<string, T>? SelectChanges<T>(IReadOnlyDictionary<string, T> entries,
        Func<string, bool> containsKey, Func<int, bool?> confirmReplacement)
    {
        var matches = entries.Keys.Where(containsKey).ToHashSet(StringComparer.Ordinal);
        if (matches.Count == 0) return entries;

        return confirmReplacement(matches.Count) switch
        {
            true => entries,
            false => entries.Where(pair => !matches.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            null => null
        };
    }
}
