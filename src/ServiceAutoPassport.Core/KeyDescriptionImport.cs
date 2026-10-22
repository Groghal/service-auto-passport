namespace ServiceAutoPassport.Core;

public static class KeyDescriptionImport
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;

            var separator = line.IndexOfAny(['\t', ' ']);
            if (separator < 0)
                throw new InvalidDataException($"Строка {index + 1}: укажите ключ и описание через табуляцию или пробел.");

            var key = line[..separator].Trim();
            var description = line[(separator + 1)..].Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace) || description.Length == 0)
                throw new InvalidDataException($"Строка {index + 1}: нужны ключ без пробелов и непустое описание.");

            if (result.Count == 0 && IsHeader(key, description)) continue;
            if (!result.TryAdd(key, description))
                throw new InvalidDataException($"Строка {index + 1}: ключ «{key}» уже встречался выше.");
        }

        if (result.Count == 0)
            throw new InvalidDataException("Нет пар «ключ — описание» для импорта.");
        return result;
    }

    private static bool IsHeader(string key, string description) =>
        (key.Equals("key", StringComparison.OrdinalIgnoreCase) &&
         description.Equals("description", StringComparison.OrdinalIgnoreCase)) ||
        (key.Equals("ключ", StringComparison.OrdinalIgnoreCase) &&
         description.Equals("описание", StringComparison.OrdinalIgnoreCase));
}
