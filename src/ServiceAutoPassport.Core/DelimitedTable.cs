using System.Text;

namespace ServiceAutoPassport.Core;

internal static class DelimitedTable
{
    public static string Write(IEnumerable<string[]> rows) =>
        string.Join("\r\n", rows.Select(row => string.Join(';', row.Select(Quote))));

    public static IEnumerable<(long Number, string[] Cells)> Read(string text, char separator)
    {
        var index = 0;
        long line = 1;
        while (index < text.Length)
        {
            var number = line;
            var cells = new List<string>();
            while (true)
            {
                var start = index;
                while (index < text.Length && text[index] == ' ') index++;
                if (index < text.Length && text[index] == '"')
                    cells.Add(ReadQuoted(text, separator, number, ref index, ref line));
                else
                {
                    while (index < text.Length && text[index] != separator && text[index] is not ('\r' or '\n')) index++;
                    cells.Add(text[start..index]);
                }

                if (index == text.Length || text[index] != separator) break;
                index++;
            }

            if (index < text.Length)
            {
                if (text[index++] == '\r' && index < text.Length && text[index] == '\n') index++;
                line++;
            }
            if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell))) yield return (number, cells.ToArray());
        }
    }

    public static string FirstLine(string text) =>
        text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').Split(['\r', '\n'], 2)[0];

    public static char DetectSeparator(string text)
    {
        var quoted = false;
        var separator = '\t';
        foreach (var character in FirstLine(text))
        {
            if (character == '"') quoted = !quoted;
            if (quoted) continue;
            if (character == '\t') return '\t';
            if (character == ';') separator = ';';
        }
        return separator;
    }

    public static bool HasHeader(string text, string[] header, char separator) =>
        FirstLine(text).Split(separator).Select(cell => cell.Trim().Trim('"'))
            .SequenceEqual(header, StringComparer.OrdinalIgnoreCase);

    public static bool ReadFlag(string value, long row, string name) => value.Trim().ToLowerInvariant() switch
    {
        "да" or "1" or "true" => true,
        "нет" or "0" or "false" => false,
        _ => throw new InvalidDataException($"Строка {row}: в колонке «{name}» укажите «Да» или «Нет».")
    };

    public static string WriteFlag(bool value) => value ? "Да" : "Нет";

    private static string ReadQuoted(string text, char separator, long number, ref int index, ref long line)
    {
        index++;
        var value = new StringBuilder();
        while (index < text.Length)
        {
            var character = text[index++];
            if (character == '"')
            {
                if (index < text.Length && text[index] == '"') index++;
                else
                {
                    while (index < text.Length && text[index] == ' ') index++;
                    if (index == text.Length || text[index] == separator || text[index] is '\r' or '\n')
                        return value.ToString();
                    break;
                }
            }
            if (character == '\n' || character == '\r' && (index == text.Length || text[index] != '\n')) line++;
            value.Append(character);
        }
        throw new InvalidDataException($"Строка {number}: проверьте кавычки и разделители.");
    }

    private static string Quote(string cell) => cell.IndexOfAny([';', '\t', '\r', '\n', '"']) >= 0
        ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : cell;
}
