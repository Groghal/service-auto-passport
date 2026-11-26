namespace ServiceAutoPassport.Core;

public sealed record KeyDescriptionEntry(string Description, bool IsHtml);

public static class KeyDescriptionImport
{
    private static readonly string[] TransferHeader = ["Ключ из J2", "Описание", "HTML"];

    public static string Export(IReadOnlyDictionary<string, KeyDescriptionEntry> entries) =>
        DelimitedTable.Write(new[] { TransferHeader }.Concat(entries.Select(pair =>
            new[] { pair.Key, pair.Value.Description, DelimitedTable.WriteFlag(pair.Value.IsHtml) })));

    public static IReadOnlyDictionary<string, KeyDescriptionEntry> ParseEntries(string text)
    {
        var separator = DelimitedTable.DetectSeparator(text);
        if (!DelimitedTable.HasHeader(text, TransferHeader, separator))
            return Parse(text).ToDictionary(pair => pair.Key,
                pair => new KeyDescriptionEntry(pair.Value, DescriptionHtml.LooksLikeMarkup(pair.Value)), StringComparer.Ordinal);

        var entries = new Dictionary<string, KeyDescriptionEntry>(StringComparer.Ordinal);
        foreach (var (number, cells) in DelimitedTable.Read(text.TrimStart('\uFEFF'), separator).Skip(1))
        {
            if (cells.Length != TransferHeader.Length)
                throw new InvalidDataException($"Строка {number}: нужны колонки «Ключ из J2», «Описание» и «HTML».");
            var key = cells[0].Trim();
            var description = cells[1].Trim();
            if (key.Length == 0 || description.Length == 0)
                throw new InvalidDataException($"Строка {number}: нужны ключ и непустое описание.");
            var entry = new KeyDescriptionEntry(description, DelimitedTable.ReadFlag(cells[2], number, "HTML"));
            if (!entries.TryAdd(key, entry))
                throw new InvalidDataException($"Строка {number}: ключ «{key}» уже встречался выше.");
        }
        if (entries.Count == 0) throw new InvalidDataException("Нет пар «ключ — описание» для импорта.");
        return entries;
    }

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        text = text.TrimStart('\uFEFF');
        var firstLine = DelimitedTable.FirstLine(text);
        var firstField = firstLine.Split(';', 2)[0].Trim();
        var useSemicolon = DelimitedTable.DetectSeparator(text) == ';' &&
            (firstField.StartsWith('"') || firstField.Equals("Ключ из J2", StringComparison.OrdinalIgnoreCase) ||
             !firstField.Any(char.IsWhiteSpace));
        var rows = useSemicolon ? DelimitedTable.Read(text, ';') : ReadLegacyPairs(text);
        foreach (var (number, cells) in rows)
        {
            if (cells.Length != 2)
                throw new InvalidDataException($"Строка {number}: укажите ключ и описание через табуляцию, точку с запятой или пробел.");

            var key = cells[0].Trim();
            var description = cells[1].Trim();
            if (result.Count == 0 && IsHeader(key, description)) continue;
            if (key.Length == 0 || key.Any(char.IsWhiteSpace) || description.Length == 0)
                throw new InvalidDataException($"Строка {number}: нужны ключ без пробелов и непустое описание.");

            if (!result.TryAdd(key, description))
                throw new InvalidDataException($"Строка {number}: ключ «{key}» уже встречался выше.");
        }

        if (result.Count == 0)
            throw new InvalidDataException("Нет пар «ключ — описание» для импорта.");
        return result;
    }

    private static IEnumerable<(long Number, string[] Cells)> ReadLegacyPairs(string text)
    {
        using var reader = new StringReader(text);
        for (long number = 1; reader.ReadLine() is { } line; number++)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return (number, line.Trim().Split(['\t', ' '], 2));
        }
    }

    private static bool IsHeader(string key, string description) =>
        (key.Equals("key", StringComparison.OrdinalIgnoreCase) &&
         description.Equals("description", StringComparison.OrdinalIgnoreCase)) ||
        ((key.Equals("ключ", StringComparison.OrdinalIgnoreCase) || key.Equals("Ключ из J2", StringComparison.OrdinalIgnoreCase)) &&
         description.Equals("описание", StringComparison.OrdinalIgnoreCase));
}
