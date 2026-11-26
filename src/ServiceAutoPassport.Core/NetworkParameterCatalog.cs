using System.Text.RegularExpressions;

namespace ServiceAutoPassport.Core;

public sealed record NetworkParameterEdit(string Label, string Block)
{
    public string? ParentBlock { get; init; }
    public bool LabelIsAutomatic { get; init; }
    public bool BlockIsAutomatic { get; init; }
    public NetworkParameterSource? Source { get; init; }
}

public sealed record NetworkParameterSource(string? J2, string? Service, string? PathVariables, bool AwaitingConnection = false);

public static partial class NetworkParameterCatalog
{
    private const string AddressPrefix = "parameter:";
    private const string LiteralPrefix = "literal:";
    private static readonly Regex Variable = new(@"\A[A-Za-z_][A-Za-z0-9_]*\z");

    internal static string AddressKey(IEnumerable<string> variables) =>
        AddressPrefix + string.Join("|", variables.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    internal static string[]? ParseAddressVariables(string text)
    {
        var variables = text.Split(',', StringSplitOptions.TrimEntries);
        return variables.All(Variable.IsMatch) ? variables : null;
    }

    internal static string LiteralKey(string serviceId, string path) => LiteralPrefix + serviceId + ":" + path;

    internal static bool IsLiteralKey(string key) => key.StartsWith(LiteralPrefix, StringComparison.Ordinal);

    public static string DisplayParameter(string key, NetworkTargetSettings settings, NetworkParameterSource? source = null)
    {
        if (key.StartsWith(AddressPrefix, StringComparison.Ordinal))
            return key[AddressPrefix.Length..].Replace("|", ", ", StringComparison.Ordinal);
        if (!IsLiteralKey(key)) return key;

        var context = key[LiteralPrefix.Length..].Split(':', 2);
        return context.Length == 2
            ? $"{source?.PathVariables ?? settings.UrlPathParameters ?? "Без переменной"} — " +
              $"{source?.Service ?? settings.SourceService ?? context[0]} · {context[1]}"
            : key;
    }

    public static IReadOnlyDictionary<string, NetworkParameterEdit> ParseExcel(string text,
        IReadOnlyDictionary<string, NetworkTargetSettings> targets,
        IReadOnlyDictionary<string, NetworkParameterEdit>? draft = null)
    {
        var literalKeys = targets.Where(pair => IsLiteralKey(pair.Key))
            .ToLookup(pair => DisplayParameter(pair.Key, pair.Value), pair => pair.Key, StringComparer.Ordinal);
        var edits = new Dictionary<string, NetworkParameterEdit>(StringComparer.Ordinal);
        var separator = DelimitedTable.DetectSeparator(text);
        var first = true;
        foreach (var (lineNumber, columns) in DelimitedTable.Read(text.TrimStart('\uFEFF'), separator))
        {
            if (columns.Length is < 3 or > 4)
                throw new InvalidDataException($"Строка {lineNumber}: нужны три колонки Excel, включая «Блок». Четвёртая колонка «Блок-родитель» необязательна.");

            var parameter = columns[0].Trim();
            var label = columns[1].Trim();
            var block = columns[2].Trim();
            var isHeader = first && (parameter.Equals("Найденный параметр", StringComparison.OrdinalIgnoreCase) ||
                                     parameter.Equals("Параметр", StringComparison.OrdinalIgnoreCase)) &&
                label.Equals("Название элемента", StringComparison.OrdinalIgnoreCase) &&
                block.Equals("Блок", StringComparison.OrdinalIgnoreCase) &&
                (columns.Length == 3 || columns[3].Trim().Equals(NetworkTableTransformer.ParentBlockColumn, StringComparison.OrdinalIgnoreCase));
            first = false;
            if (isHeader) continue;
            if (label.Length == 0)
                throw new InvalidDataException($"Строка {lineNumber}: укажите название элемента.");

            var variables = ParseAddressVariables(parameter);
            string key;
            if (variables is not null) key = AddressKey(variables);
            else
            {
                var matches = literalKeys[parameter].ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException(matches.Length == 0
                        ? $"Строка {lineNumber}: укажите J2-переменную или найденное подключение без переменной."
                        : $"Строка {lineNumber}: найдено несколько подключений с таким именем. Измените их в таблице.");
                key = matches[0];
            }

            var edit = new NetworkParameterEdit(label, block)
            {
                ParentBlock = columns.Length == 4 ? columns[3].Trim() : KeepParent(key, block)
            };
            if (!edits.TryAdd(key, edit))
                throw new InvalidDataException($"Строка {lineNumber}: параметр «{parameter}» уже указан выше.");
        }
        if (edits.Count == 0) throw new InvalidDataException("Нет строк с параметрами для вставки.");
        return edits;

        string? KeepParent(string key, string block)
        {
            if (draft is not null)
                return draft.TryGetValue(key, out var edit) && SameBlock(edit.Block, block) ? edit.ParentBlock : null;
            return targets.TryGetValue(key, out var target) && SameBlock(target.Block, block) ? target.ParentBlock : null;
        }
    }

    internal static bool SameBlock(string? first, string? second) =>
        string.Equals(first?.Trim() ?? "", second?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    public static void SaveEdits(NetworkMapSettings settings, IReadOnlyDictionary<string, NetworkParameterEdit> edits, Action save) =>
        Save(settings, edits, [], null, save);

    public static void SaveCatalog(NetworkMapSettings settings, IReadOnlyDictionary<string, NetworkParameterEdit> entries,
        NetworkAnalysis? analysis, Action save)
    {
        var removed = settings.Targets.Keys.Where(key => !entries.ContainsKey(key)).ToArray();
        Save(settings, entries, removed, analysis, save);
    }

    private static void Save(NetworkMapSettings settings, IReadOnlyDictionary<string, NetworkParameterEdit> edits,
        IReadOnlyList<string> removed, NetworkAnalysis? analysis, Action save)
    {
        var current = settings.Targets;
        var active = analysis?.Connections.GroupBy(c => c.TargetKey)
            .ToDictionary(g => g.Key, g => g.MinBy(c => c.Path, StringComparer.Ordinal)!, StringComparer.Ordinal);
        NetworkTargetSettings? FindTarget(string key) =>
            current.GetValueOrDefault(key) ?? active?.GetValueOrDefault(key)?.ExternalTarget;

        var staged = new Dictionary<string, NetworkTargetSettings>(current, StringComparer.Ordinal);
        foreach (var key in removed) staged.Remove(key);
        foreach (var (key, edit) in edits)
        {
            var previous = FindTarget(key);
            staged[key] = new NetworkTargetSettings
            {
                Label = edit.Label, Block = edit.Block, ParentBlock = edit.ParentBlock,
                LabelIsAutomatic = edit.LabelIsAutomatic, BlockIsAutomatic = edit.BlockIsAutomatic,
                SourceJ2 = previous?.SourceJ2 ?? edit.Source?.J2,
                AwaitingConnection = previous?.AwaitingConnection ?? edit.Source?.AwaitingConnection ?? false,
                SourceService = previous?.SourceService ?? edit.Source?.Service,
                UrlPathParameters = previous?.UrlPathParameters ?? edit.Source?.PathVariables
            };
        }

        settings.Targets = staged;
        try { save(); }
        catch
        {
            settings.Targets = current;
            throw;
        }

        // The active analysis holds references to these settings.
        foreach (var key in edits.Keys)
        {
            var existing = FindTarget(key);
            if (existing is null) continue;
            var entry = staged[key];
            CopyLabels(entry, existing);
            existing.SourceJ2 = entry.SourceJ2;
            existing.AwaitingConnection = entry.AwaitingConnection;
            existing.SourceService = entry.SourceService;
            existing.UrlPathParameters = entry.UrlPathParameters;
            staged[key] = existing;
        }

        foreach (var key in removed)
        {
            var connection = active?.GetValueOrDefault(key);
            if (connection is null) continue;
            var target = connection.ExternalTarget;
            target.Label = connection.SuggestedTargetLabel;
            target.LabelIsAutomatic = true;
            target.Block = NetworkTargetSettings.DefaultBlock;
            target.ParentBlock = null;
            target.BlockIsAutomatic = true;
        }
    }
}
