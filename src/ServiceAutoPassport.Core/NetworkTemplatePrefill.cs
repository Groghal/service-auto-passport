using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ServiceAutoPassport.Core;

public sealed record NetworkTemplatePrefillResult(
    IReadOnlyDictionary<string, NetworkParameterEdit> Edits, IReadOnlyList<string> Skipped)
{
    public string? Source { get; init; }
}

public static class NetworkTemplatePrefill
{
    private sealed record TemplateRow(string Label, string? Block, string? ParentBlock);
    private static readonly Regex PortName = new(@"(?:^|[_-])(?i:port)\z|(?<=[a-z0-9])(?:Port|PORT)\z");
    private static string CellText(HtmlNode node) => WebUtility.HtmlDecode(string.Concat(node.Descendants()
        .Where(child => child.NodeType == HtmlNodeType.Text || child.Name == "br")
        .Select(child => child.Name == "br" ? " " : child.InnerText))).Trim();

    public static NetworkTemplatePrefillResult ReadFirstWithRows(IEnumerable<(string Name, string? Html)> sources,
        IReadOnlyList<NetworkConnection> connections, IReadOnlyDictionary<string, NetworkParameterEdit> draft)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var (name, html) in sources)
        {
            if (string.IsNullOrWhiteSpace(html) || !seen.Add(html)) continue;
            try
            {
                var result = Read(html, connections, draft);
                if (result.Edits.Count > 0 || result.Skipped.Count > 0)
                    return result with { Source = name };
            }
            catch (InvalidDataException error)
            {
                errors.Add($"{name}: {error.Message}");
            }
        }

        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return new NetworkTemplatePrefillResult(new Dictionary<string, NetworkParameterEdit>(), []);
    }

    public static NetworkTemplatePrefillResult Read(string template, IReadOnlyList<NetworkConnection> connections,
        IReadOnlyDictionary<string, NetworkParameterEdit> draft)
    {
        var (_, table) = NetworkTableTransformer.Find(template);
        var rows = table.Descendants("tr").Where(row => row.Ancestors("table").FirstOrDefault() == table)
            .Select(row => row.ChildNodes.Where(cell => cell.Name is "th" or "td").ToArray()).ToArray();
        var edits = new Dictionary<string, NetworkParameterEdit>(StringComparer.Ordinal);
        var skipped = new List<string>();
        if (rows.Length <= 1 || rows.Skip(1).All(cells => cells.All(cell => CellText(cell).Length == 0)))
            return new(edits, skipped);

        if (rows[0].Any(SpansCells))
            throw new InvalidDataException("Для предзаполнения нужны заголовки без объединённых ячеек.");
        var headers = rows[0].Select(CellText).ToArray();
        var labelIndex = Column(headers, NetworkTableTransformer.FirstColumn);
        var nodeLabelIndex = Column(headers, NetworkTableTransformer.NodeLabelColumn);
        var addressIndex = Column(headers, NetworkTableTransformer.AddressColumn);
        var parametersIndex = Column(headers, NetworkTableTransformer.ParametersColumn);
        var blockIndex = Column(headers, NetworkTableTransformer.BlockColumn);
        var parentBlockIndex = Column(headers, NetworkTableTransformer.ParentBlockColumn);
        if (labelIndex < 0 || addressIndex < 0 && parametersIndex < 0)
            throw new InvalidDataException("В исходной таблице нужны «Элемент» и «Адресный параметр» или «Параметры».");

        var knownParameters = BuildLegacyLookup(connections);
        var knownPorts = connections.SelectMany(c => J2Variables.Extract(c.Port)).ToHashSet(StringComparer.Ordinal);
        var byKey = new Dictionary<string, List<(int Number, TemplateRow Row)>>(StringComparer.Ordinal);

        for (var i = 1; i < rows.Length; i++)
        {
            var cells = rows[i];
            if (cells.All(cell => CellText(cell).Length == 0)) continue;
            var number = i + 1;
            if (cells.Length != headers.Length || cells.Any(SpansCells))
            {
                skipped.Add($"Строка {number}: колонки не совпадают с заголовками.");
                continue;
            }

            var nodeLabel = nodeLabelIndex < 0 ? "" : CellText(cells[nodeLabelIndex]);
            var label = nodeLabel.Length > 0 ? nodeLabel : CellText(cells[labelIndex]);
            var parameter = CellText(cells[addressIndex >= 0 ? addressIndex : parametersIndex]);
            if (label.Length == 0)
            {
                skipped.Add($"Строка {number}: нет названия элемента.");
                continue;
            }

            var variables = NetworkParameterCatalog.ParseAddressVariables(parameter);
            var key = variables is null ? null : addressIndex >= 0
                ? NetworkParameterCatalog.AddressKey(variables)
                : ResolveLegacy(variables, knownParameters, knownPorts, draft);
            if (key is null)
            {
                skipped.Add($"Строка {number}: адресный параметр не определён однозначно.");
                continue;
            }

            if (!byKey.TryGetValue(key, out var candidates)) byKey[key] = candidates = new();
            var block = blockIndex < 0 ? null : CellText(cells[blockIndex]);
            var parentBlock = parentBlockIndex < 0 ? "" : CellText(cells[parentBlockIndex]);
            candidates.Add((number, new TemplateRow(label, block, parentBlock.Length == 0 ? null : parentBlock)));
        }

        foreach (var (key, candidates) in byKey)
        {
            var row = candidates[0].Row;
            if (candidates.Any(candidate => candidate.Row != row))
            {
                skipped.AddRange(candidates.Select(c => $"Строка {c.Number}: для параметра в шаблоне заданы разные значения."));
                continue;
            }

            if (!draft.TryGetValue(key, out var current))
            {
                edits[key] = new NetworkParameterEdit(row.Label, row.Block ?? NetworkTargetSettings.DefaultBlock)
                {
                    ParentBlock = row.ParentBlock,
                    BlockIsAutomatic = row.Block is null
                };
                continue;
            }

            var replaceBlock = current.BlockIsAutomatic && string.IsNullOrEmpty(current.ParentBlock) && row.Block is not null;
            var block = replaceBlock ? row.Block! : current.Block;
            var next = current with
            {
                Label = current.LabelIsAutomatic ? row.Label : current.Label,
                LabelIsAutomatic = false,
                Block = block,
                ParentBlock = current.ParentBlock ?? (NetworkParameterCatalog.SameBlock(block, row.Block ?? NetworkTargetSettings.DefaultBlock)
                    ? row.ParentBlock : null),
                BlockIsAutomatic = current.BlockIsAutomatic && !replaceBlock
            };
            if (next != current) edits[key] = next;
            else skipped.AddRange(candidates.Select(c => $"Строка {c.Number}: нет автоматических значений для замены."));
        }
        return new(edits, skipped);
    }

    private static bool SpansCells(HtmlNode cell) =>
        cell.GetAttributeValue("colspan", 1) != 1 || cell.GetAttributeValue("rowspan", 1) != 1;

    private static int Column(string[] headers, string name)
    {
        var index = Array.FindIndex(headers, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && Array.FindLastIndex(headers, value => value.Equals(name, StringComparison.OrdinalIgnoreCase)) != index)
            throw new InvalidDataException($"Заголовок «{name}» встречается в шаблоне несколько раз.");
        return index;
    }

    private static Dictionary<string, HashSet<string>> BuildLegacyLookup(IEnumerable<NetworkConnection> connections)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var group in connections.GroupBy(connection => connection.TargetKey))
        {
            void Add(IEnumerable<NetworkConnection> calls)
            {
                var parameters = calls.SelectMany(c => c.ParameterKeys).ToArray();
                if (parameters.Length == 0) return;
                var signature = NetworkParameterCatalog.AddressKey(parameters);
                if (!lookup.TryGetValue(signature, out var targets)) lookup[signature] = targets = new(StringComparer.Ordinal);
                targets.Add(group.Key);
            }

            foreach (var connection in group) Add([connection]);
            Add(group);
            Add(group.Where(connection => connection.Choice.Include));
        }
        return lookup;
    }

    private static string? ResolveLegacy(string[] variables, Dictionary<string, HashSet<string>> lookup,
        HashSet<string> knownPorts, IReadOnlyDictionary<string, NetworkParameterEdit> draft)
    {
        var signature = NetworkParameterCatalog.AddressKey(variables);
        if (lookup.TryGetValue(signature, out var candidates))
        {
            if (candidates.Count != 1) return null;
            var key = candidates.Single();
            return NetworkParameterCatalog.IsLiteralKey(key) ? null : key;
        }

        return variables.Length == 1 && (draft.ContainsKey(signature) ||
            !knownPorts.Contains(variables[0]) && !PortName.IsMatch(variables[0])) ? signature : null;
    }
}
