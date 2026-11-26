using System.Text;

namespace ServiceAutoPassport.Core;

internal static class TabSeparatedTable
{
    public static string Write(IEnumerable<string[]> rows) =>
        string.Join("\r\n", rows.Select(row => string.Join('\t', row.Select(Quote))));

    public static IEnumerable<(long Number, string[] Cells)> Read(string text)
    {
        var index = 0;
        long line = 1;
        while (index < text.Length)
        {
            var number = line;
            var cells = new List<string>();
            while (true)
            {
                if (index < text.Length && text[index] == '"')
                    cells.Add(ReadQuoted(text, number, ref index, ref line));
                else
                {
                    var start = index;
                    while (index < text.Length && text[index] is not ('\t' or '\r' or '\n')) index++;
                    cells.Add(text[start..index]);
                }

                if (index == text.Length || text[index] != '\t') break;
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

    public static bool HasHeader(string text, string[] header) =>
        text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').Split(['\r', '\n'], 2)[0]
            .Split('\t').Select(cell => cell.Trim().Trim('"')).SequenceEqual(header, StringComparer.OrdinalIgnoreCase);

    public static bool ReadFlag(string value, long row, string name) => value.Trim().ToLowerInvariant() switch
    {
        "да" or "1" or "true" => true,
        "нет" or "0" or "false" => false,
        _ => throw new InvalidDataException($"Строка {row}: в колонке «{name}» укажите «Да» или «Нет».")
    };

    public static string WriteFlag(bool value) => value ? "Да" : "Нет";

    private static string ReadQuoted(string text, long number, ref int index, ref long line)
    {
        index++;
        var value = new StringBuilder();
        while (index < text.Length)
        {
            var character = text[index++];
            if (character == '"')
            {
                if (index == text.Length || text[index] is '\t' or '\r' or '\n')
                    return value.ToString();
                if (text[index] != '"') break;
                index++;
            }
            if (character == '\n' || character == '\r' && (index == text.Length || text[index] != '\n')) line++;
            value.Append(character);
        }
        throw new InvalidDataException($"Строка {number}: проверьте кавычки и разделители.");
    }

    private static string Quote(string cell) => cell.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
        ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : cell;
}
